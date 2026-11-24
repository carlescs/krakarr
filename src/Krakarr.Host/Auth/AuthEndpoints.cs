using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth;

public sealed record CredentialsRequest(string Username, string Password);

/// <summary>Changes the signed-in user's own username and, when <paramref name="NewPassword"/> is set, password.</summary>
public sealed record ChangeCredentialsRequest(string Username, string CurrentPassword, string? NewPassword);

/// <param name="SetupToken">One-time token from the startup log or the config folder's <c>setup-token</c> file.</param>
public sealed record SetupRequest(string Username, string Password, string? SetupToken);

public static class AuthEndpoints
{
    /// <summary>Per-IP rate limit applied to the login, setup and password endpoints.</summary>
    public const string RateLimitPolicy = "auth";
    private const string LogCategory = "Krakarr.Auth";

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").AllowAnonymous();

        group.MapGet("/status", async (HttpContext http, KrakarrDbContext db, CancellationToken ct) => new
        {
            SetupRequired = !await db.Users.AnyAsync(ct),
            Authenticated = http.User.Identity?.IsAuthenticated == true,
            Username = http.User.Identity?.Name,
            Role = http.User.Identity?.IsAuthenticated == true ? http.User.GetRole() : (UserRole?)null,
        });

        group.MapPost("/setup", async (SetupRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher,
            SetupTokenStore setupToken, ILoggerFactory loggers, CancellationToken ct) =>
        {
            // Serialize setup so concurrent requests cannot both pass the "no users yet" check.
            await setupToken.Lock.WaitAsync(ct);
            try
            {
                if (await db.Users.AnyAsync(ct))
                {
                    return Results.Problem("Setup has already been completed", statusCode: StatusCodes.Status409Conflict);
                }

                if (!setupToken.Validate(req.SetupToken))
                {
                    loggers.CreateLogger(LogCategory).LogWarning("Rejected setup attempt with an invalid setup token from {Ip}", ClientIp(http));
                    return Results.Problem(
                        "Invalid setup token. It is printed in the Krakarr log at startup and saved as 'setup-token' in the config folder.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                if (Validate(new CredentialsRequest(req.Username, req.Password)) is { } problem) return problem;

                var user = new User { Username = req.Username.Trim(), PasswordHash = string.Empty, Role = UserRole.Admin };
                user.PasswordHash = hasher.HashPassword(user, req.Password);
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
                setupToken.Consume();
                await SignInAsync(http, user);
                return Results.NoContent();
            }
            finally
            {
                setupToken.Lock.Release();
            }
        }).RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/login", async (CredentialsRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher,
            LoginThrottle throttle, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var username = (req.Username ?? string.Empty).Trim();
            var password = req.Password ?? string.Empty;
            var logger = loggers.CreateLogger(LogCategory);

            if (throttle.RetryAfter(username) is { } wait)
            {
                var seconds = (int)Math.Ceiling(wait.TotalSeconds);
                http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                logger.LogWarning("Rejected login for locked username {Username} from {Ip}", username, ClientIp(http));
                KrakarrTelemetry.Logins.Add(1, new KeyValuePair<string, object?>(KrakarrTelemetry.OutcomeTag, "locked"));
                return Results.Problem($"Too many failed login attempts. Try again in {seconds} seconds.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
            var result = PasswordVerificationResult.Failed;
            if (user is null)
            {
                // Hash anyway to keep timing similar for unknown users.
                hasher.HashPassword(new User { Username = "x", PasswordHash = "x" }, password);
            }
            else
            {
                result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            }

            if (user is null || result == PasswordVerificationResult.Failed)
            {
                throttle.RecordFailure(username);
                logger.LogWarning("Failed login for username {Username} from {Ip}", username, ClientIp(http));
                KrakarrTelemetry.Logins.Add(1, new KeyValuePair<string, object?>(KrakarrTelemetry.OutcomeTag, KrakarrTelemetry.Failure));
                return Results.Problem("Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RecordSuccess(username);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, password);
                await db.SaveChangesAsync(ct);
            }

            await SignInAsync(http, user);
            KrakarrTelemetry.Logins.Add(1, new KeyValuePair<string, object?>(KrakarrTelemetry.OutcomeTag, KrakarrTelemetry.Success));
            return Results.NoContent();
        }).RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthConstants.CookieScheme);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/auth/password", async (ChangeCredentialsRequest req, HttpContext http, KrakarrDbContext db,
            IPasswordHasher<User> hasher, UserSessions sessions, CancellationToken ct) =>
        {
            var id = http.User.GetUserId();
            var user = id is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? string.Empty) == PasswordVerificationResult.Failed)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is incorrect"] });
            }

            if ((ValidateUsername(req.Username) ?? ValidatePassword(req.NewPassword, optional: true)) is { } problem) return problem;
            var username = req.Username.Trim();
            if (await UsernameTakenAsync(db, username, user.Id, ct)) return UsernameTaken();

            user.Username = username;
            if (!string.IsNullOrEmpty(req.NewPassword))
            {
                user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
                user.SecurityStamp = User.NewSecurityStamp(); // signs out every other session
            }

            await db.SaveChangesAsync(ct);
            sessions.Invalidate(user.Id);
            await SignInAsync(http, user); // the current password was just checked: a fresh sign-in
            return Results.NoContent();
        }).WithTags("Auth").RequireRateLimiting(RateLimitPolicy);

        // "Sign out everywhere else": rotating the stamp invalidates every cookie of this user; this one is re-issued.
        app.MapPost("/api/v1/auth/sessions/revoke", async (HttpContext http, KrakarrDbContext db, UserSessions sessions,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var id = http.User.GetUserId();
            var user = id is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            user.SecurityStamp = User.NewSecurityStamp();
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(user.Id);
            await SignInAsync(http, user, http.User.GetSignedInAt()); // not a fresh sign-in: keeps the absolute lifetime
            loggers.CreateLogger(LogCategory).LogInformation("{Username} signed out their other sessions from {Ip}", user.Username, ClientIp(http));
            return Results.NoContent();
        }).WithTags("Auth");

        return app;
    }

    public const int MinPasswordLength = 8;

    private static IResult? Validate(CredentialsRequest req) => ValidateUsername(req.Username) ?? ValidatePassword(req.Password);

    internal static IResult? ValidateUsername(string? username) =>
        string.IsNullOrWhiteSpace(username) ? Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Username is required"] })
        : username.Trim().Length > 100 ? Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["Username must be at most 100 characters"] })
        : null;

    internal static IResult? ValidatePassword(string? password, bool optional = false) =>
        (optional && string.IsNullOrEmpty(password)) || password?.Length >= MinPasswordLength
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = [$"Password must be at least {MinPasswordLength} characters"] });

    /// <summary>Usernames are unique ignoring case, so "Admin" and "admin" can't both exist.</summary>
    internal static Task<bool> UsernameTakenAsync(KrakarrDbContext db, string username, int? exceptId, CancellationToken ct)
    {
        var lower = username.ToLowerInvariant();
        return db.Users.AnyAsync(u => u.Username.ToLower() == lower && u.Id != exceptId, ct);
    }

    internal static IResult UsernameTaken() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = ["That username is already taken"] });

    internal static Task SignInAsync(HttpContext http, User user, DateTimeOffset? signedInAt = null) =>
        http.SignInAsync(AuthConstants.CookieScheme,
            UserClaims.Create(user.Id, user.Username, user.Role, user.SecurityStamp,
                signedInAt ?? http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()),
            new AuthenticationProperties { IsPersistent = true });
}
