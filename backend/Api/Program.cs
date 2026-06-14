using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Rolling, structured (compact JSON) log file — one file per day, Information and up.
// Also ships to Seq when SEQ_URL is set (deploy stacks); every event is tagged
// with the app version so logs are filterable per release.
var seqUrl = Environment.GetEnvironmentVariable("SEQ_URL");
var appVersion = Environment.GetEnvironmentVariable("APP_VERSION") ?? "dev";

var logConfig = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Fuel")
    .Enrich.WithProperty("Version", appVersion)
    .WriteTo.Console()
    .WriteTo.File(
        formatter: new CompactJsonFormatter(),
        path: "logs/log-.json",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31);

if (!string.IsNullOrWhiteSpace(seqUrl))
    logConfig = logConfig.WriteTo.Seq(seqUrl);

Log.Logger = logConfig.CreateLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

// Traces → Seq via OTLP. Endpoint is either explicit (OTEL_EXPORTER_OTLP_ENDPOINT)
// or derived from SEQ_URL (Seq listens for OTLP on port 5341).
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
if (string.IsNullOrWhiteSpace(otlpEndpoint) && !string.IsNullOrWhiteSpace(seqUrl))
{
    var seqUri = new Uri(seqUrl);
    // Full signal path: the exporter uses a programmatic Endpoint as-is and
    // does not append /v1/traces (it only does that for the env-var form).
    otlpEndpoint = $"{seqUri.Scheme}://{seqUri.Host}:5341/ingest/otlp/v1/traces";
}

if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("Fuel", serviceVersion: appVersion))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(otlpEndpoint);
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
            }));
}

builder.Services.AddOpenApi();
builder.Services.AddControllers();

var connectionString = $"Host={builder.Configuration["DB_HOST"] ?? "localhost"};" +
    $"Port={builder.Configuration["DB_PORT"] ?? "5432"};" +
    $"Database={builder.Configuration["DB_NAME"] ?? "fuel"};" +
    $"Username={builder.Configuration["DB_USER"] ?? "postgres"};" +
    $"Password={builder.Configuration["DB_PASSWORD"] ?? "postgres"}";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IFoodService, FoodService>();
builder.Services.AddScoped<IProfileService, ProfileService>();

// SMTP from flat SMTP_* env keys (same style as DB_*); password stays in the env file.
builder.Services.Configure<Api.Config.SmtpOptions>(o =>
{
    o.Host = builder.Configuration["SMTP_HOST"] ?? "";
    o.Port = int.TryParse(builder.Configuration["SMTP_PORT"], out var p) ? p : 465;
    o.User = builder.Configuration["SMTP_USER"] ?? "";
    o.Password = builder.Configuration["SMTP_PASS"] ?? "";
    o.From = builder.Configuration["SMTP_FROM"] ?? builder.Configuration["SMTP_USER"] ?? "";
    o.AcceptAllCerts = string.Equals(builder.Configuration["SMTP_ACCEPT_ALL_CERTS"], "true",
        StringComparison.OrdinalIgnoreCase);
});
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<ReleaseNotifier>();
builder.Services.AddHostedService<BackupService>();

// AI nutrition estimator — provider chosen at deploy time from flat AI_* keys (the
// API key stays in the env file). Wired only when enabled + configured; otherwise a
// no-op estimator keeps DI resolvable and the UI shows AI as off. See docs/ai-providers.md.
var aiOptions = new Api.Config.AiOptions
{
    Provider = builder.Configuration["AI_PROVIDER"] ?? "deepseek",
    ApiKey = builder.Configuration["AI_API_KEY"] ?? "",
    BaseUrl = builder.Configuration["AI_BASE_URL"] ?? "https://api.deepseek.com",
    Model = builder.Configuration["AI_MODEL"] ?? "deepseek-v4-flash",
    Enabled = string.Equals(builder.Configuration["AI_ENABLED"], "true", StringComparison.OrdinalIgnoreCase),
    TimeoutSeconds = int.TryParse(builder.Configuration["AI_TIMEOUT_SECONDS"], out var aiTimeout) ? aiTimeout : 30,
    ClaudeEnabled = string.Equals(builder.Configuration["AI_CLAUDE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase),
    ClaudeApiKey = builder.Configuration["AI_CLAUDE_API_KEY"] ?? "",
    ClaudeModel = builder.Configuration["AI_CLAUDE_MODEL"] ?? "claude-haiku-4-5-20251001",
};
builder.Services.AddSingleton(aiOptions);

// Multiprovider: DeepSeek for text (cheap, $0.14/M) → Claude Haiku for images ($1/M).
// Each provider gets its own typed client with resilience; a composite estimator
// delegates text→DeepSeek, image→Claude. If only one is on, it handles both methods
// (DeepSeek images fail at the API level — 400 — because v4-flash is text-only, but
// the abstraction allows a future text+vision DeepSeek model to plug in with no
// code changes).

var hasDs = aiOptions.Enabled && !string.IsNullOrWhiteSpace(aiOptions.ApiKey);
var hasCl = aiOptions.ClaudeEnabled && !string.IsNullOrWhiteSpace(aiOptions.ClaudeApiKey);

if (!hasDs && !hasCl)
{
    builder.Services.AddSingleton<INutritionEstimator, DisabledNutritionEstimator>();
    if (aiOptions.Enabled)
        Log.Warning("AI_ENABLED is true but no provider/key is configured — AI estimation disabled.");
}
else
{
    // DeepSeek for text
    INutritionEstimator? textEst = null;
    INutritionEstimator? imageEst = null;

    if (hasDs)
    {
        builder.Services.AddHttpClient<DeepSeekEstimator>(c =>
        {
            c.BaseAddress = new Uri(aiOptions.BaseUrl.TrimEnd('/') + "/");
            c.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddResilienceHandler("ai-ds", b =>
        {
            b.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, aiOptions.TimeoutSeconds)));
            b.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromMilliseconds(250),
                UseJitter = true,
            });
        });
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<DeepSeekEstimator>() as INutritionEstimator);
    }

    // Claude for images (Phase 3) — separate key, separate client, separate pipeline
    if (hasCl)
    {
        builder.Services.AddHttpClient<ClaudeEstimator>(c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com/");
            c.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddResilienceHandler("ai-cl", b =>
        {
            b.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, aiOptions.TimeoutSeconds)));
            b.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.FromMilliseconds(250),
                UseJitter = true,
            });
        });
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<ClaudeEstimator>() as INutritionEstimator);
    }

    // Composite: text→DeepSeek (or Claude if only Claude is on), image→Claude (or
    // DeepSeek if only DeepSeek is on — will fail at the API level for now, but the
    // abstraction allows a future DeepSeek vision model with no code changes).
    builder.Services.AddSingleton<INutritionEstimator>(sp =>
    {
        INutritionEstimator? t = null, i = null;
        if (hasDs) t = sp.GetRequiredService<DeepSeekEstimator>();
        if (hasCl) i = sp.GetRequiredService<ClaudeEstimator>();
        return new CompositeNutritionEstimator(
            textProvider: t ?? i!,       // fall back to the other if only one is configured
            imageProvider: i ?? t!);
    });
}

var app = builder.Build();

// Apply any pending EF Core migrations on startup. CI applies them as an
// explicit deploy step too; this is an idempotent safety net (no-op if already
// applied).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Serve the bundled SPA (built frontend copied into wwwroot) and fall back to
// index.html for client-side routes. API controllers are matched first; the
// fallback only handles non-/api, non-file requests.
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

try
{
    Log.Information("Starting Fuel API");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
