using Api.Data;
using Api.Services;
using Microsoft.EntityFrameworkCore;
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
