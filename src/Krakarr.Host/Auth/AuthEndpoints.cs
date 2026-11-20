using System.Security.Claims;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth;

public sealed record CredentialsRequest(string Username, string Password);

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

                var user = new User { Username = req.Username.Trim(), PasswordHash = string.Empty };
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
                return Results.Problem("Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.RecordSuccess(username);
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, password);
                await db.SaveChangesAsync(ct);
            }

            await SignInAsync(http, user);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimitPolicy);

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(AuthConstants.CookieScheme);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/auth/password", async (CredentialsRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            if (Validate(req) is { } problem) return problem;
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == http.User.Identity!.Name, ct);
            if (user is null) return Results.NotFound();
            user.Username = req.Username.Trim();
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            await db.SaveChangesAsync(ct);
            await SignInAsync(http, user);
            return Results.NoContent();
        }).WithTags("Auth").RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    private static IResult? Validate(CredentialsRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(req.Username)) errors["username"] = ["Username is required"];
        if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 8) errors["password"] = ["Password must be at least 8 characters"];
        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    private static Task SignInAsync(HttpContext http, User user)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Username), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            AuthConstants.CookieScheme);
        return http.SignInAsync(AuthConstants.CookieScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }
}
