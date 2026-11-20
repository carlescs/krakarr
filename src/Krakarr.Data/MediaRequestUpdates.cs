using Krakarr.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

public static class MediaRequestUpdates
{
    /// <summary>
    /// Marks pending requests for a title approved once it is in the library, however it got there (approving the
    /// request, adding it directly or a library import). Returns how many requests changed.
    /// </summary>
    public static Task<int> ApproveMatchingAsync(KrakarrDbContext db, MediaType type, int? tmdbId, int? tvdbId, CancellationToken ct = default)
    {
        if (tmdbId is null && tvdbId is null) return Task.FromResult(0);
        var now = DateTimeOffset.UtcNow;
        return db.MediaRequests
            .Where(r => r.Status == RequestStatus.Pending && r.MediaType == type
                && ((tmdbId != null && r.TmdbId == tmdbId) || (tvdbId != null && r.TvdbId == tvdbId)))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Approved).SetProperty(r => r.ProcessedAt, now), ct);
    }
}
