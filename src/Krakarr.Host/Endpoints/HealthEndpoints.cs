using Krakarr.Core.Domain;
using Krakarr.Core.Health;
using Krakarr.Core.Notifications;

namespace Krakarr.Host.Endpoints;

public static class HealthEndpoints
{
    /// <summary>
    /// Admin only (see <c>RoleAccess</c>): problems include paths. The anonymous <c>/api/v1/health</c> stays a plain
    /// liveness probe for Docker and reverse proxies.
    /// </summary>
    public const string Path = "/api/v1/system/health";

    /// <summary>Configuration whose changes can fix or cause problems: a successful write re-runs the checks.</summary>
    private static readonly string[] WatchedPaths =
    [
        "/api/v1/settings",
        "/api/v1/rootfolders",
        "/api/v1/indexers",
        "/api/v1/downloads/client",
    ];

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Path).WithTags("System");

        // The latest report; the checks run every few minutes and after configuration changes.
        group.MapGet("/", (IHealthMonitor health) => health.Current);

        // Runs every check now, validating API keys again instead of using the remembered outcome.
        group.MapPost("/check", async (IHealthMonitor health, HealthValidationCache cache, CancellationToken ct) =>
        {
            cache.Clear();
            return await health.CheckNowAsync(ct);
        });

        return app;
    }

    /// <summary>Asks for a health run after any successful change to the configuration the checks look at.</summary>
    public static IApplicationBuilder UseHealthRefresh(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        await next(http);
        var request = http.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || http.Response.StatusCode >= 400) return;
        var path = request.Path;
        if (WatchedPaths.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            http.RequestServices.GetRequiredService<IHealthMonitor>().RequestCheck();
        }
    });
}

/// <summary>Admins: one bell notification per health problem, dated when it appeared so a dismissal lasts until it comes back.</summary>
public sealed class HealthNotificationSource(IHealthMonitor health) : INotificationSource
{
    public Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        if (context.Role != UserRole.Admin) return Task.FromResult<IReadOnlyList<Notification>>([]);
        IReadOnlyList<Notification> notifications = health.Current.Issues
            .Select(i => new Notification("health." + i.Id, "health", i.CheckName, i.Message, "/settings/health", i.Count,
                Severity: i.Severity == HealthSeverity.Error ? NotificationSeverity.Error : NotificationSeverity.Warning,
                Date: i.Since))
            .ToList();
        return Task.FromResult(notifications);
    }
}
