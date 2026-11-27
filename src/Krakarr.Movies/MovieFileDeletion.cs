using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.MediaServers;
using Krakarr.Data;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Movies;

/// <summary>Deletes movie files from disk and from the library.</summary>
public static class MovieFileDeletion
{
    /// <summary>
    /// Deletes a movie file (video plus subtitle sidecars) and unlinks it from its movie, optionally unmonitoring the
    /// movie so the next search doesn't fetch it again. Returns false when the file doesn't exist.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file's path is outside the movie folder.</exception>
    /// <exception cref="IOException">The file could not be deleted; the library is left unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be deleted; the library is left unchanged.</exception>
    public static async Task<bool> DeleteAsync(KrakarrDbContext db, IEventHub eventHub, IMediaServerNotifier mediaServers, int movieFileId, bool unmonitor, CancellationToken ct)
    {
        var file = await db.MovieFiles.AsNoTracking()
            .Where(f => f.Id == movieFileId)
            .Select(f => new { f.Id, f.MovieId, f.RelativePath, f.QualityId })
            .FirstOrDefaultAsync(ct);
        if (file is null) return false;
        var folder = await db.Movies.AsNoTracking().Where(m => m.Id == file.MovieId).Select(m => m.Path).FirstAsync(ct);

        LibraryFiles.DeleteVideo(folder, file.RelativePath);
        mediaServers.PathChanged(folder);

        await db.Movies.Where(m => m.MovieFileId == movieFileId).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(m => m.MovieFileId, (int?)null);
            if (unmonitor) s.SetProperty(m => m.Monitored, false);
        }, ct);
        // Subtitle rows cascade with the file.
        await db.MovieFiles.Where(f => f.Id == movieFileId).ExecuteDeleteAsync(ct);

        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.FileDeleted,
            SourceTitle = file.RelativePath,
            MovieId = file.MovieId,
            QualityId = file.QualityId,
            Message = unmonitor ? "Deleted manually; movie unmonitored" : "Deleted manually",
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        await eventHub.PublishAsync(EventNames.MovieUpdated, new { Id = file.MovieId }, ct);
        return true;
    }
}
