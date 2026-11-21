using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Auth;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Endpoints;

/// <summary>A library title the caller follows.</summary>
public sealed record SubscriptionDto(MediaType MediaType, int LibraryId, string Title, int Year, string? PosterUrl, DateTimeOffset SubscribedAt);

/// <summary>Whether the caller follows a title, and (for staff) who does.</summary>
public sealed record SubscriptionState(bool Subscribed, IReadOnlyList<string>? Subscribers);

public sealed record SubscriberDto(int Id, string Username, int Count);

/// <summary>
/// Users subscribe to library movies and series to see them in their calendar and "My titles" filters. Every
/// signed-in user manages their own subscriptions; listing who follows what is for staff (<see cref="RoleAccess"/>).
/// </summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/subscriptions").WithTags("Subscriptions");

        // The caller's subscriptions, newest first. Bounded by MediaSubscriptions.MaxPerUser.
        group.MapGet("/", async (HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId) return Results.Ok(Array.Empty<SubscriptionDto>());
            var rows = await db.MediaSubscriptions.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderByDescending(s => s.Id)
                .Select(s => s.MovieId != null
                    ? new SubscriptionDto(MediaType.Movie, s.MovieId.Value, s.Movie!.Title, s.Movie.Year, s.Movie.PosterUrl, s.CreatedAt)
                    : new SubscriptionDto(MediaType.Series, s.SeriesId!.Value, s.Series!.Title, s.Series.Year, s.Series.PosterUrl, s.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // Staff (RoleAccess): users who follow at least one title, for the calendar's "whose titles" picker.
        group.MapGet("/users", async (KrakarrDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Users.AsNoTracking()
                .Where(u => db.MediaSubscriptions.Any(s => s.UserId == u.Id))
                .OrderBy(u => u.Username)
                .Select(u => new SubscriberDto(u.Id, u.Username, db.MediaSubscriptions.Count(s => s.UserId == u.Id)))
                .ToListAsync(ct)));

        group.MapGet("/{mediaType}/{id:int}", async (MediaType mediaType, int id, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(mediaType)) return Results.NotFound();
            var userId = http.User.GetUserId();
            var subscribers = MediaSubscriptions.For(db, mediaType, id).AsNoTracking();
            var subscribed = userId is not null && await subscribers.AnyAsync(s => s.UserId == userId, ct);
            IReadOnlyList<string>? names = IsStaff(http)
                ? await subscribers.OrderBy(s => s.User!.Username).Select(s => s.User!.Username).ToListAsync(ct)
                : null;
            return Results.Ok(new SubscriptionState(subscribed, names));
        });

        group.MapPut("/{mediaType}/{id:int}", async (MediaType mediaType, int id, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId)
                return Results.Problem("Subscriptions belong to user accounts, not the API key.", statusCode: StatusCodes.Status400BadRequest);
            if (!Enum.IsDefined(mediaType)) return Results.NotFound();
            var exists = mediaType == MediaType.Movie
                ? await db.Movies.AnyAsync(m => m.Id == id, ct)
                : await db.Series.AnyAsync(s => s.Id == id, ct);
            if (!exists) return Results.NotFound();

            if (!await MediaSubscriptions.For(db, mediaType, id).AnyAsync(s => s.UserId == userId, ct)
                && await db.MediaSubscriptions.CountAsync(s => s.UserId == userId, ct) >= MediaSubscriptions.MaxPerUser)
            {
                return Results.Problem($"You can follow at most {MediaSubscriptions.MaxPerUser} titles.", statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await MediaSubscriptions.SubscribeAsync(db, [userId], mediaType, id, ct);
            }
            catch (DbUpdateException)
            {
                // A concurrent subscribe won the unique index: already subscribed.
            }

            return Results.NoContent();
        });

        group.MapDelete("/{mediaType}/{id:int}", async (MediaType mediaType, int id, HttpContext http, KrakarrDbContext db, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not { } userId || !Enum.IsDefined(mediaType)) return Results.NoContent();
            await MediaSubscriptions.For(db, mediaType, id).Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    private static bool IsStaff(HttpContext http) => http.User.GetRole() is UserRole.Admin or UserRole.Manager;
}
