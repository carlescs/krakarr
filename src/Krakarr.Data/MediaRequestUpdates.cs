using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

public static class MediaRequestUpdates
{
    /// <summary>
    /// Marks pending requests for a title approved once it is in the library, however it got there (approving the
    /// request, adding it directly or a library import), and subscribes their requesters to library title
    /// <paramref name="libraryId"/>. Returns how many requests changed.
    /// </summary>
    public static async Task<int> ApproveMatchingAsync(KrakarrDbContext db, MediaType type, int? tmdbId, int? tvdbId, int libraryId, CancellationToken ct = default)
    {
        if (tmdbId is null && tvdbId is null) return 0;
        var matching = db.MediaRequests
            .Where(r => r.Status == RequestStatus.Pending && r.MediaType == type
                && ((tmdbId != null && r.TmdbId == tmdbId) || (tvdbId != null && r.TvdbId == tvdbId)));

        var requesters = await matching.Select(r => r.RequestedById).Distinct().ToListAsync(ct);
        if (requesters.Count == 0) return 0;
        await MediaSubscriptions.SubscribeAsync(db, requesters, type, libraryId, ct);

        var now = DateTimeOffset.UtcNow;
        return await matching.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Approved).SetProperty(r => r.ProcessedAt, now), ct);
    }
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
