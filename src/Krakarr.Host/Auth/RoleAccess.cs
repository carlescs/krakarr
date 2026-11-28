using Krakarr.Core.Domain;

namespace Krakarr.Host.Auth;

/// <summary>
/// Which role a request needs, decided from its method and path. Kept in one table instead of spread over the modules
/// so the rules are easy to review, and so that endpoints added later are closed to plain users by default: anything
/// not listed here needs at least <see cref="UserRole.Manager"/>.
/// </summary>
public static class RoleAccess
{
    /// <summary>Settings, credentials and anything that exposes paths or secrets: admins only, any method.</summary>
    private static readonly string[] AdminOnly =
    [
        "/api/v1/settings",
        "/api/v1/users",
        "/api/v1/indexers",
        "/api/v1/filesystem",
        "/api/v1/downloads/client",
        "/api/v1/system/status",
        "/api/v1/system/tasks",
        "/api/v1/system/health",
        "/api/v1/system/backups",
        "/api/v1/system/database",
        "/api/v1/series/finished", // unmonitors every finished series at once
    ];

    /// <summary>Readable by managers (they pick them when adding titles or searching subtitles), changed/tested only by admins.</summary>
    private static readonly string[] AdminWrites =
    [
        "/api/v1/qualityprofiles",
        "/api/v1/customformats",
        "/api/v1/rootfolders",
        "/api/v1/subtitles/providers",
    ];

    /// <summary>Open to every signed-in user, any method (sign out, own password, notifications, live events).</summary>
    private static readonly string[] Everyone =
    [
        "/api/v1/auth",
        "/api/v1/health",
        "/api/v1/notifications", // each source filters by role
        "/api/v1/push", // own devices only; topics are limited to the caller's role
        "/api/v1/system/background-tasks", // each user sees the tasks they started
        "/hubs",
    ];

    /// <summary>Read-only library and Discover views for plain users (GET only).</summary>
    private static readonly string[] UserReads =
    [
        "/api/v1/movies",
        "/api/v1/series",
        "/api/v1/calendar",
        "/api/v1/people",
        "/api/v1/keywords",
        "/api/v1/ratings",
        "/api/v1/qualities",
        "/api/v1/qualityprofiles",
        "/api/v1/languages",
        "/api/v1/subtitles/languages",
        "/api/v1/subtitles/files",
        "/api/v1/subtitles/wanted-languages",
    ];

    /// <summary>
    /// Path segments under movies/series that trigger indexer searches or touch the disk: not for plain users.
    /// (<c>lookup</c> only asks TMDB/TheTVDB, so users may search for titles to request.)
    /// </summary>
    private static readonly HashSet<string> StaffSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "import", "releases", "organize",
    };

    /// <summary>
    /// Requests: every user may list, create and withdraw their own (the endpoints check ownership); approving and
    /// declining is for staff.
    /// </summary>
    private const string Requests = "/api/v1/requests";

    private static readonly HashSet<string> RequestStaffSegments = new(StringComparer.OrdinalIgnoreCase) { "approve", "decline" };

    /// <summary>
    /// Subscriptions: every user follows and unfollows titles for themselves (the endpoints use the caller's id);
    /// listing which users follow titles is for staff.
    /// </summary>
    private const string Subscriptions = "/api/v1/subscriptions";

    /// <summary>The least privileged role allowed to make this request.</summary>
    public static UserRole RequiredRole(string method, string path)
    {
        path = path.TrimEnd('/');
        if (!Under(path, "/api") && !Under(path, "/hubs")) return UserRole.User;
        if (Everyone.Any(p => Under(path, p))) return UserRole.User;
        if (AdminOnly.Any(p => Under(path, p))) return UserRole.Admin;
        if (Under(path, Subscriptions)) return Under(path, Subscriptions + "/users") ? UserRole.Manager : UserRole.User;
        if (Under(path, Requests)) return path.Split('/').Any(RequestStaffSegments.Contains) ? UserRole.Manager : UserRole.User;

        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        if (!read && AdminWrites.Any(p => Under(path, p))) return UserRole.Admin;
        if (read && UserReads.Any(p => Under(path, p)) && !path.Split('/').Any(StaffSegments.Contains)) return UserRole.User;
        return UserRole.Manager;
    }

    public static bool IsAllowed(UserRole role, string method, string path) => role <= RequiredRole(method, path);

    /// <summary>True when <paramref name="path"/> is <paramref name="prefix"/> or below it (segment-wise).</summary>
    private static bool Under(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}

public static class RoleAccessMiddleware
{
    /// <summary>Answers 403 when a signed-in caller's role is below what <see cref="RoleAccess"/> requires.</summary>
    public static IApplicationBuilder UseRoleAccess(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (http.User.Identity?.IsAuthenticated == true
            && !RoleAccess.IsAllowed(http.User.GetRole(), http.Request.Method, http.Request.Path.Value ?? "/"))
        {
            await Results.Problem("You don't have permission to do this.", statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(http);
            return;
        }

        await next(http);
    });
}
