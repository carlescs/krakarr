using System.Globalization;
using System.Security.Claims;
using Krakarr.Core.Domain;
using Krakarr.Core.Push;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

public static class MediaRequestUpdates
{
    /// <summary>
    /// Marks pending requests for a title approved once it is in the library, however it got there (approving the
    /// request, adding it directly or a library import), subscribes their requesters to library title
    /// <paramref name="libraryId"/> and flags the result for their notification bell. Returns how many requests changed.
    /// </summary>
    /// <param name="push">Tells each requester on their phone, when given.</param>
    public static async Task<int> ApproveMatchingAsync(KrakarrDbContext db, MediaType type, int? tmdbId, int? tvdbId, int libraryId,
        CancellationToken ct = default, IPushNotifier? push = null)
    {
        if (tmdbId is null && tvdbId is null) return 0;
        var matching = db.MediaRequests
            .Where(r => r.Status == RequestStatus.Pending && r.MediaType == type
                && ((tmdbId != null && r.TmdbId == tmdbId) || (tvdbId != null && r.TvdbId == tvdbId)));

        // A title has a request or two at most (one pending request per title, plus duplicates by TheTVDB id).
        var handled = await matching.Select(r => new { r.RequestedById, r.Title, r.Year }).ToListAsync(ct);
        if (handled.Count == 0) return 0;
        await MediaSubscriptions.SubscribeAsync(db, handled.Select(r => r.RequestedById).Distinct().ToList(), type, libraryId, ct);

        var now = DateTimeOffset.UtcNow;
        var changed = await matching.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Approved).SetProperty(r => r.ProcessedAt, now)
            .SetProperty(r => r.ResultUnseen, true), ct);
        foreach (var r in handled) push?.Send(RequestPush.Result(r.RequestedById, r.Title, r.Year, approved: true, type, libraryId, null));
        return changed;
    }
}

/// <summary>Push notifications about requests, shared by the request endpoints and <see cref="MediaRequestUpdates"/>.</summary>
public static class RequestPush
{
    /// <summary>Tells <paramref name="userId"/> how their request was handled; approved ones open the library title.</summary>
    public static PushMessage Result(int userId, string title, int year, bool approved, MediaType type, int? libraryId, string? reason)
    {
        var name = year > 0 ? $"{title} ({year})" : title;
        var link = approved && libraryId is { } id ? (type == MediaType.Movie ? $"/movies/{id}" : $"/series/{id}") : "/requests";
        return approved
            ? new PushMessage(PushKind.RequestResult, "Request approved", $"{name} was added to the library. You'll be told when it's ready to watch.",
                link, UserId: userId)
            : new PushMessage(PushKind.RequestResult, "Request declined", reason is null ? name : $"{name}: {reason}", link, UserId: userId);
    }

    public static PushMessage Created(string username, string title, int year, MediaType type) =>
        new(PushKind.RequestCreated, "New request", $"{username} requested the {(type == MediaType.Movie ? "movie" : "series")} {(year > 0 ? $"{title} ({year})" : title)}",
            "/requests", "requests");
}

public static class MediaSubscriptions
{
    /// <summary>Most titles one user may follow; keeps the "My titles" filters bounded.</summary>
    public const int MaxPerUser = 5000;

    /// <summary>Subscriptions of <paramref name="userId"/> to library titles of <paramref name="type"/>.</summary>
    public static IQueryable<MediaSubscription> Of(KrakarrDbContext db, int userId, MediaType type) =>
        type == MediaType.Movie
            ? db.MediaSubscriptions.Where(s => s.UserId == userId && s.MovieId != null)
            : db.MediaSubscriptions.Where(s => s.UserId == userId && s.SeriesId != null);

    public static IQueryable<MediaSubscription> For(KrakarrDbContext db, MediaType type, int libraryId) =>
        type == MediaType.Movie
            ? db.MediaSubscriptions.Where(s => s.MovieId == libraryId)
            : db.MediaSubscriptions.Where(s => s.SeriesId == libraryId);

    /// <summary>The signed-in user's id from their <see cref="ClaimTypes.NameIdentifier"/> claim, or null for API keys.</summary>
    public static int? GetUserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), CultureInfo.InvariantCulture, out var userId) ? userId : null;

    /// <summary>
    /// Makes whoever added a title follow it. Does nothing for the API key, which is no user (the user id is the
    /// sign-in's <see cref="ClaimTypes.NameIdentifier"/> claim).
    /// </summary>
    public static Task<int> FollowAsAdderAsync(KrakarrDbContext db, ClaimsPrincipal adder, MediaType type, int libraryId, CancellationToken ct = default) =>
        GetUserId(adder) is { } userId
            ? SubscribeAsync(db, [userId], type, libraryId, ct)
            : Task.FromResult(0);

    /// <summary>Subscribes users to a library title, skipping those already subscribed. Returns how many were added.</summary>
    public static async Task<int> SubscribeAsync(KrakarrDbContext db, IReadOnlyCollection<int> userIds, MediaType type, int libraryId, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return 0;
        var existing = await For(db, type, libraryId).Where(s => userIds.Contains(s.UserId)).Select(s => s.UserId).ToListAsync(ct);
        var added = 0;
        foreach (var userId in userIds.Except(existing))
        {
            db.MediaSubscriptions.Add(new MediaSubscription
            {
                UserId = userId,
                MovieId = type == MediaType.Movie ? libraryId : null,
                SeriesId = type == MediaType.Series ? libraryId : null,
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}
