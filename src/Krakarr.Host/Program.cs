using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Data.Postgres;
using Krakarr.Data.Sqlite;
using Krakarr.Downloads;
using Krakarr.Host.Auth;
using Krakarr.Host.Endpoints;
using Krakarr.Host.Hosting;
using Krakarr.Host.Hubs;
using Krakarr.Host.Integrations;
using Krakarr.Host.Telemetry;
using Krakarr.Indexers;
using Krakarr.Media;
using Krakarr.Movies;
using Krakarr.Series;
using Krakarr.Subtitles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var inContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";

// ---- Paths & bootstrap configuration -------------------------------------------------------
var configDir = builder.Configuration["Krakarr:ConfigDirectory"]
    ?? (inContainer ? "/config" : Path.Combine(builder.Environment.ContentRootPath, "data"));
configDir = Path.GetFullPath(configDir);
builder.Configuration.AddJsonFile(Path.Combine(configDir, "config.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(); // environment wins over config.json

var paths = new KrakarrPaths
{
    ConfigDirectory = configDir,
    DefaultDownloadDirectory = Path.GetFullPath(builder.Configuration["Krakarr:DownloadDirectory"]
        ?? (inContainer ? "/data/downloads" : Path.Combine(configDir, "downloads"))),
};
paths.EnsureCreated();
builder.Services.AddSingleton(paths);

var urlBase = "/" + (builder.Configuration["Krakarr:UrlBase"] ?? string.Empty).Trim('/');
if (urlBase == "/") urlBase = string.Empty;

if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_HTTP_PORTS"])
    && string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]))
{
    builder.WebHost.UseUrls($"http://*:{builder.Configuration["Krakarr:Port"] ?? "8787"}");
}

// ---- Database ------------------------------------------------------------------------------
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
builder.Services.AddDbContext<KrakarrDbContext>(options =>
{
    switch (dbOptions.Provider.ToLowerInvariant())
    {
        case "postgres" or "postgresql" or "npgsql":
            options.UseKrakarrPostgres(dbOptions.ConnectionString
                ?? throw new InvalidOperationException("Database:ConnectionString is required for PostgreSQL"));
            break;
        case "sqlite":
            options.UseKrakarrSqlite(dbOptions.ConnectionString ?? $"Data Source={Path.Combine(configDir, "krakarr.db")}");
            break;
        default:
            throw new InvalidOperationException($"Unknown database provider '{dbOptions.Provider}' (use sqlite or postgres)");
    }
});

// ---- Modules -------------------------------------------------------------------------------
builder.Services.AddKrakarrData();
builder.Services.AddKrakarrScheduling();
builder.Services.AddKrakarrMedia();
builder.Services.AddKrakarrIndexers();
builder.Services.AddKrakarrDownloads();
builder.Services.AddKrakarrMovies();
builder.Services.AddKrakarrSeries(builder.Configuration);
builder.Services.AddKrakarrSubtitles();
builder.Services.AddKrakarrJellyfin();
builder.Services.AddKrakarrTelemetry();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<Krakarr.Core.Notifications.INotificationSource, RequestNotificationSource>();
builder.Services.AddScoped<Krakarr.Core.Notifications.INotificationSource, ReleaseNotificationSource>();

// ---- Web: JSON, SignalR, auth --------------------------------------------------------------
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IEventHub, SignalREventHub>();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddDataProtection()
    .SetApplicationName("Krakarr")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDir, "keys")));

var authSecurity = builder.Configuration.GetSection(AuthSecurityOptions.SectionName).Get<AuthSecurityOptions>() ?? new AuthSecurityOptions();
builder.Services
    .AddAuthentication(AuthConstants.SmartScheme)
    .AddPolicyScheme(AuthConstants.SmartScheme, "Cookie or API key", o =>
        o.ForwardDefaultSelector = ctx => ApiKeyAuthenticationHandler.GetProvidedKey(ctx.Request) is not null
            ? AuthConstants.ApiKeyScheme
            : AuthConstants.CookieScheme)
    .AddCookie(AuthConstants.CookieScheme, o =>
    {
        o.Cookie.Name = "krakarr_auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        // Krakarr__SecureCookies=true marks the cookie Secure regardless of the scheme (for HTTPS-only deployments).
        o.Cookie.SecurePolicy = builder.Configuration.GetValue<bool>("Krakarr:SecureCookies")
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        // Idle timeout; the absolute lifetime (SessionMaxAge) is enforced by UserSessions.
        o.ExpireTimeSpan = authSecurity.SessionIdleTimeout > TimeSpan.Zero ? authSecurity.SessionIdleTimeout : TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        // APIs answer 401/403 instead of redirecting to a login page.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        // Re-checks the account on every request so deletions, password resets, revoked sessions and role changes apply
        // immediately.
        o.Events.OnValidatePrincipal = UserSessions.ValidateAsync;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthConstants.ApiKeyScheme, null);

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(AuthConstants.AdminPolicy, p => p.RequireRole(nameof(UserRole.Admin)));
builder.Services.AddSingleton<UserSessions>();

// Brute-force protection: per-IP rate limit on credential endpoints + per-username lockout (LoginThrottle).
builder.Services.Configure<AuthSecurityOptions>(builder.Configuration.GetSection(AuthSecurityOptions.SectionName));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<SetupTokenStore>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx =>
    {
        var limit = Math.Max(1, ctx.RequestServices.GetRequiredService<IOptions<AuthSecurityOptions>>().Value.AttemptsPerMinutePerIp);
        return RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        });
    });
    o.OnRejected = (ctx, _) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Krakarr.Auth")
            .LogWarning("Rate limited {Path} from {Ip}", ctx.HttpContext.Request.Path, ctx.HttpContext.Connection.RemoteIpAddress);
        return ValueTask.CompletedTask;
    };
});

// Only trusted reverse proxies may set X-Forwarded-For/Proto (loopback by default, plus Krakarr__TrustedProxies);
// otherwise clients could spoof their IP (rate limits, logs) and the scheme (Secure cookie). Parsed eagerly so a
// typo fails at startup instead of silently trusting nobody.
var trustedProxies = builder.Configuration[TrustedProxies.SettingName];
TrustedProxies.Parse(trustedProxies);
builder.Services.Configure<ForwardedHeadersOptions>(o => TrustedProxies.Configure(o, trustedProxies));

var app = builder.Build();

// ---- Database migration & seed -------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().InitializeAsync();
    var setupRequired = !await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Users.AnyAsync();
    app.Services.GetRequiredService<SetupTokenStore>().Initialize(setupRequired);
}

app.Logger.LogInformation("Krakarr {Version} starting. Config: {Config}, DB: {Provider}", SystemEndpoints.Version, configDir, dbOptions.Provider);

// ---- Pipeline ------------------------------------------------------------------------------
app.UseUntrustedProxyWarning();
app.UseForwardedHeaders();
app.UseSecurityHeaders(app.Configuration);
if (!string.IsNullOrEmpty(urlBase))
{
    app.UsePathBase(urlBase);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseKrakarrStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRoleAccess();
app.UseRateLimiter();

app.MapOpenApi("/api/openapi/{documentName}.json");
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapSystemEndpoints();
app.MapDashboardEndpoints();
app.MapSettingsEndpoints();
app.MapIndexerEndpoints();
app.MapDownloadEndpoints();
app.MapMovieEndpoints();
app.MapSeriesEndpoints();
app.MapSubtitleEndpoints();
app.MapCalendarEndpoints();
app.MapPeopleEndpoints();
app.MapRequestEndpoints();
app.MapSubscriptionEndpoints();
app.MapNotificationEndpoints();
app.MapJellyfinEndpoints();
app.MapTelemetryEndpoints();
app.MapHub<EventsHub>("/hubs/events");
app.MapKrakarrSpa(urlBase);

await app.RunAsync();

public partial class Program;
