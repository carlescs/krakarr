using System.Security.Claims;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth;

public sealed record CredentialsRequest(string Username, string Password);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth").AllowAnonymous();

        group.MapGet("/status", async (HttpContext http, KrakarrDbContext db, CancellationToken ct) => new
        {
            SetupRequired = !await db.Users.AnyAsync(ct),
            Authenticated = http.User.Identity?.IsAuthenticated == true,
            Username = http.User.Identity?.Name,
        });

        group.MapPost("/setup", async (CredentialsRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            if (await db.Users.AnyAsync(ct))
            {
                return Results.Problem("Setup has already been completed", statusCode: StatusCodes.Status409Conflict);
            }

            if (Validate(req) is { } problem) return problem;

            var user = new User { Username = req.Username.Trim(), PasswordHash = string.Empty };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            await SignInAsync(http, user);
            return Results.NoContent();
        });

        group.MapPost("/login", async (CredentialsRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == req.Username.Trim(), ct);
            if (user is null)
            {
                // Hash anyway to keep timing similar for unknown users.
                hasher.HashPassword(new User { Username = "x", PasswordHash = "x" }, req.Password ?? string.Empty);
                return Results.Problem("Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? string.Empty);
            if (result == PasswordVerificationResult.Failed)
            {
                return Results.Problem("Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
            }

            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password!);
                await db.SaveChangesAsync(ct);
            }

            await SignInAsync(http, user);
            return Results.NoContent();
        });

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
        }).WithTags("Auth");

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
