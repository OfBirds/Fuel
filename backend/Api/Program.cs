using Api.Authorization;
using Api.Data;
using Api.Services;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
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
builder.Services.AddControllers(o => o.Filters.Add<ResourceOwnershipFilter>());

// Signed-JWT auth. The signing key follows the flat-env convention (JWT_SIGNING_KEY);
// when unset we mint an ephemeral random key so local dev needs no setup — tokens just
// don't survive a restart, and a warning fires. A real key MUST be set in deploy stacks.
var jwtKeyRaw = builder.Configuration["JWT_SIGNING_KEY"];
byte[] jwtKeyBytes;
if (string.IsNullOrWhiteSpace(jwtKeyRaw))
{
    jwtKeyBytes = RandomNumberGenerator.GetBytes(32);
    Log.Warning("JWT_SIGNING_KEY not set — using an ephemeral signing key. Tokens are " +
        "invalidated on restart; set JWT_SIGNING_KEY (>=32 chars) before deploying.");
}
else
{
    jwtKeyBytes = Encoding.UTF8.GetBytes(jwtKeyRaw);
    if (jwtKeyBytes.Length < 32)
        throw new InvalidOperationException(
            "JWT_SIGNING_KEY must be at least 32 bytes (256 bits) for HMAC-SHA256.");
}
var jwtSigningKey = new SymmetricSecurityKey(jwtKeyBytes);
var jwtExpiryDays = int.TryParse(builder.Configuration["JWT_EXPIRY_DAYS"], out var ed) && ed > 0 ? ed : 30;
builder.Services.AddSingleton<ITokenService>(
    new JwtTokenService(jwtSigningKey, TimeSpan.FromDays(jwtExpiryDays)));

// Dual-scheme auth for the CrimsonRaven migration (docs/auth-crimsonraven.md):
//   "Fuel"         — the self-issued HMAC JWT (sub = Fuel User.Id); the backup path.
//   "CrimsonRaven" — OIDC access tokens validated against the IdP's JWKS (OIDC_AUTHORITY),
//                    mapped onto a Fuel User by OidcUserProvisioner.
// The default "smart" policy scheme peeks the bearer's `iss` and forwards to whichever
// validator matches, so one Authorization header works for both. OIDC is opt-in: when
// OIDC_AUTHORITY is unset (e.g. a stack without an IdP yet) only the Fuel scheme runs.
const string FuelScheme = "Fuel";
const string OidcScheme = "CrimsonRaven";

var oidcAuthority = builder.Configuration["OIDC_AUTHORITY"];   // e.g. http://192.168.4.55:9100
var oidcAudience = builder.Configuration["OIDC_AUDIENCE"];     // expected `aud` in the access token
var oidcEnabled = !string.IsNullOrWhiteSpace(oidcAuthority);

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "smart";
    options.DefaultChallengeScheme = "smart";
});

authBuilder.AddPolicyScheme("smart", "Fuel or CrimsonRaven (by token issuer)", options =>
{
    options.ForwardDefaultSelector = ctx =>
    {
        if (oidcEnabled)
        {
            string authHeader = ctx.Request.Headers.Authorization!;
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var token = new JsonWebTokenHandler().ReadJsonWebToken(authHeader["Bearer ".Length..].Trim());
                    if (string.Equals(token.Issuer, oidcAuthority, StringComparison.OrdinalIgnoreCase))
                        return OidcScheme;
                }
                catch { /* unreadable token → let the Fuel validator reject it */ }
            }
        }
        return FuelScheme;
    };
});

authBuilder.AddJwtBearer(FuelScheme, options =>
{
    options.MapInboundClaims = false; // keep `sub` as `sub`, don't remap to NameIdentifier
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = JwtTokenService.Issuer,
        ValidateAudience = true,
        ValidAudience = JwtTokenService.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = jwtSigningKey,
        ValidateLifetime = true,
        NameClaimType = JwtRegisteredClaimNames.Sub,
    };
});

if (oidcEnabled)
{
    authBuilder.AddJwtBearer(OidcScheme, options =>
    {
        options.Authority = oidcAuthority;
        // homelab :9100 is http; the prod instance (raven.bearsoft.duckdns.org) is https.
        options.RequireHttpsMetadata = oidcAuthority!.StartsWith("https", StringComparison.OrdinalIgnoreCase);
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = oidcAuthority,
            ValidateAudience = !string.IsNullOrWhiteSpace(oidcAudience),
            ValidAudience = oidcAudience,
            ValidateLifetime = true,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    });

    // Maps a CrimsonRaven identity onto a Fuel User (link-by-verified-email) and rewrites
    // `sub` to the Fuel User.Id so ResourceOwnershipFilter + routes stay unchanged.
    // Needs HttpContext (for the bearer) to resolve email from the IdP userinfo endpoint.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IClaimsTransformation, OidcUserProvisioner>();
}

// Locked-down by default: every endpoint requires auth unless it opts out with
// [AllowAnonymous] (auth, version, unsubscribe) or .AllowAnonymous() (SPA fallback).
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

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

// AI nutrition estimator — a hot-reloadable registry of providers (docs/ai-providers.md).
// The provider LIST lives in a JSON file (path in AI_CONFIG_FILE), added as a
// reloadOnChange config source so reorder/enable/model edits apply live, no redeploy.
// Secret key VALUES stay in flat AI_KEY_<NAME> env vars; a provider references one by name.
// EstimatorChain resolves keys per-request and tries providers in priority order per
// modality (text/vision), falling through on failure. AnthropicEstimator and OpenAiEstimator
// implement the two wire conventions (the latter also covers self-hosted Ollama/vLLM).
var aiConfigFile = builder.Configuration["AI_CONFIG_FILE"];
if (!string.IsNullOrWhiteSpace(aiConfigFile))
    builder.Configuration.AddJsonFile(aiConfigFile, optional: true, reloadOnChange: true);

builder.Services.Configure<Api.Config.AiProvidersOptions>(builder.Configuration.GetSection("ai"));

var aiTimeout = int.TryParse(builder.Configuration["AI_TIMEOUT_SECONDS"], out var t) ? t : 30;
builder.Services.AddHttpClient("ai", c => c.Timeout = Timeout.InfiniteTimeSpan) // pipeline owns the timeout
    .AddResilienceHandler("ai-resilience", b =>
    {
        b.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, aiTimeout)));
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 1,
            BackoffType = DelayBackoffType.Constant,
            Delay = TimeSpan.FromMilliseconds(250),
            UseJitter = true,
        });
    });

builder.Services.AddSingleton<IEstimatorChain, EstimatorChain>();

// Barcode / EAN lookup — a numeric barcode → Open Food Facts → catalogue Food.
// Docs: docs/barcode-lookup.md. No API key needed (OFF is free); the named HttpClient
// carries a descriptive User-Agent + timeout. Config is flat env vars: BARCODE_ENABLED,
// BARCODE_BASE_URL, BARCODE_TIMEOUT_SECONDS.
builder.Services.Configure<Api.Config.BarcodeOptions>(o =>
{
    o.Enabled = string.Equals(builder.Configuration["BARCODE_ENABLED"], "true",
        StringComparison.OrdinalIgnoreCase);
    o.BaseUrl = builder.Configuration["BARCODE_BASE_URL"] ?? "https://world.openfoodfacts.org";
    o.TimeoutSeconds = int.TryParse(builder.Configuration["BARCODE_TIMEOUT_SECONDS"], out var bt) ? bt : 10;
});
var barcodeTimeout = int.TryParse(
    builder.Configuration["BARCODE_TIMEOUT_SECONDS"], out var bt2) ? bt2 : 10;
builder.Services.AddHttpClient("barcode", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .AddResilienceHandler("barcode-resilience", b =>
    {
        b.AddTimeout(TimeSpan.FromSeconds(Math.Max(1, barcodeTimeout)));
    });
builder.Services.AddSingleton<IBarcodeFoodLookup, OpenFoodFactsLookup>();

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
    app.MapOpenApi().AllowAnonymous();
}

// Serve the bundled SPA (built frontend copied into wwwroot) and fall back to
// index.html for client-side routes. API controllers are matched first; the
// fallback only handles non-/api, non-file requests.
app.UseStaticFiles(new StaticFileOptions
{
    // The service worker is a stable URL with mutable content — never let it be stale-cached (by
    // browsers or any proxy), or installed PWAs freeze on an old build. Always revalidate.
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.Equals("sw.js", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html").AllowAnonymous();

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
