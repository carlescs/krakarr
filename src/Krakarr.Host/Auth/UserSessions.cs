using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth;

public static class UserClaims
{
    public const string SecurityStamp = "krakarr:stamp";

    public static ClaimsPrincipal Create(int id, string username, UserRole role, string securityStamp) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role.ToString()),
                new Claim(SecurityStamp, securityStamp),
            ],
            AuthConstants.CookieScheme));

    /// <summary>The caller's role; anything unexpected falls back to the least privileged one.</summary>
    public static UserRole GetRole(this ClaimsPrincipal principal) =>
        Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role) && Enum.IsDefined(role) ? role : UserRole.User;

    public static int? GetUserId(this ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var id) ? id : null;
}

/// <summary>
/// Validates sign-in cookies against the user table on every request, so deleting a user, resetting a password or
/// changing a role takes effect immediately instead of when the 30-day cookie expires. Users are cached (one small
/// entry per account) and evicted by <see cref="Invalidate"/> whenever an account changes.
/// </summary>
public sealed class UserSessions(IServiceScopeFactory scopes)
{
    public sealed record Snapshot(string Username, UserRole Role, string SecurityStamp);

    private readonly ConcurrentDictionary<int, Snapshot> _cache = new();

    public async Task<Snapshot?> GetAsync(int userId, CancellationToken ct)
    {
        if (_cache.TryGetValue(userId, out var cached)) return cached;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var snapshot = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new Snapshot(u.Username, u.Role, u.SecurityStamp))
            .FirstOrDefaultAsync(ct);

        // Only existing users are cached, so the cache never holds more entries than there are accounts.
        if (snapshot is not null) _cache[userId] = snapshot;
        return snapshot;
    }

    public void Invalidate(int userId) => _cache.TryRemove(userId, out _);

    /// <summary>Cookie <c>OnValidatePrincipal</c> handler.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var id = principal?.GetUserId();
        var sessions = context.HttpContext.RequestServices.GetRequiredService<UserSessions>();
        var user = id is null ? null : await sessions.GetAsync(id.Value, context.HttpContext.RequestAborted);

        // Cookies issued before roles existed carry no stamp; they match the empty stamp the migration gave existing
        // users, and are invalidated as soon as that user's password changes.
        var stamp = principal?.FindFirstValue(UserClaims.SecurityStamp) ?? string.Empty;
        if (user is null || !string.Equals(stamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthConstants.CookieScheme);
            return;
        }

        if (principal!.Identity?.Name != user.Username
            || principal.FindFirstValue(ClaimTypes.Role) != user.Role.ToString()
            || principal.FindFirstValue(UserClaims.SecurityStamp) is null)
        {
            context.ReplacePrincipal(UserClaims.Create(id!.Value, user.Username, user.Role, user.SecurityStamp));
            context.ShouldRenew = true;
        }
    }
}
