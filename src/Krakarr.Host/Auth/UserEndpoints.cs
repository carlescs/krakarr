using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Auth;

public sealed record UserDto(int Id, string Username, UserRole Role, DateTimeOffset CreatedAt);

public sealed record CreateUserRequest(string Username, string Password, UserRole Role);

/// <param name="Password">New password; leave empty to keep the current one. Setting it signs the user out everywhere.</param>
public sealed record UpdateUserRequest(string Username, UserRole Role, string? Password);

/// <summary>User management (admins only; see <see cref="RoleAccess"/>).</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users").RequireAuthorization(AuthConstants.AdminPolicy);

        group.MapGet("/", (KrakarrDbContext db, CancellationToken ct) => db.Users.AsNoTracking()
            .OrderBy(u => u.Role).ThenBy(u => u.Username)
            .Select(u => new UserDto(u.Id, u.Username, u.Role, u.CreatedAt))
            .ToListAsync(ct));

        group.MapPost("/", async (CreateUserRequest req, KrakarrDbContext db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            if ((AuthEndpoints.ValidateUsername(req.Username) ?? AuthEndpoints.ValidatePassword(req.Password) ?? ValidateRole(req.Role)) is { } problem)
            {
                return problem;
            }

            var username = req.Username.Trim();
            if (await AuthEndpoints.UsernameTakenAsync(db, username, null, ct)) return AuthEndpoints.UsernameTaken();

            var user = new User { Username = username, PasswordHash = string.Empty, Role = req.Role };
            user.PasswordHash = hasher.HashPassword(user, req.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/users/{user.Id}", ToDto(user));
        });

        group.MapPut("/{id:int}", async (int id, UpdateUserRequest req, HttpContext http, KrakarrDbContext db, IPasswordHasher<User> hasher,
            UserSessions sessions, CancellationToken ct) =>
        {
            if ((AuthEndpoints.ValidateUsername(req.Username) ?? AuthEndpoints.ValidatePassword(req.Password, optional: true) ?? ValidateRole(req.Role)) is { } problem)
            {
                return problem;
            }

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();

            var username = req.Username.Trim();
            if (await AuthEndpoints.UsernameTakenAsync(db, username, id, ct)) return AuthEndpoints.UsernameTaken();

            if (user.Role == UserRole.Admin && req.Role != UserRole.Admin)
            {
                if (id == http.User.GetUserId()) return Conflict("You can't remove your own admin role. Ask another admin to do it.");
                if (!await HasOtherAdminAsync(db, id, ct)) return Conflict("Krakarr needs at least one admin.");
            }

            user.Username = username;
            user.Role = req.Role;
            if (!string.IsNullOrEmpty(req.Password))
            {
                user.PasswordHash = hasher.HashPassword(user, req.Password);
                user.SecurityStamp = User.NewSecurityStamp();
            }

            await db.SaveChangesAsync(ct);
            sessions.Invalidate(id);
            return Results.Ok(ToDto(user));
        });

        group.MapDelete("/{id:int}", async (int id, HttpContext http, KrakarrDbContext db, UserSessions sessions, CancellationToken ct) =>
        {
            if (id == http.User.GetUserId()) return Conflict("You can't delete your own account.");

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null) return Results.NotFound();
            if (user.Role == UserRole.Admin && !await HasOtherAdminAsync(db, id, ct)) return Conflict("Krakarr needs at least one admin.");

            db.Users.Remove(user);
            await db.SaveChangesAsync(ct);
            sessions.Invalidate(id);
            return Results.NoContent();
        });

        return app;
    }

    private static UserDto ToDto(User u) => new(u.Id, u.Username, u.Role, u.CreatedAt);

    private static IResult? ValidateRole(UserRole role) => Enum.IsDefined(role)
        ? null
        : Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Unknown role"] });

    private static Task<bool> HasOtherAdminAsync(KrakarrDbContext db, int exceptId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Role == UserRole.Admin && u.Id != exceptId, ct);

    private static IResult Conflict(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status409Conflict);
}
