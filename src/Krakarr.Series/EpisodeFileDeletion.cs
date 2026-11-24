using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Series;

/// <summary>Deletes episode files from disk and from the library.</summary>
public static class EpisodeFileDeletion
{
    /// <summary>
    /// Deletes the given episode files (video plus subtitle sidecars) and unlinks their episodes, optionally unmonitoring
    /// them so the next search doesn't fetch them again. Files that can't be deleted stay in the library and are
    /// reported in <see cref="BulkResult.Errors"/>; unknown ids are ignored.
    /// </summary>
    public static async Task<BulkResult> DeleteAsync(KrakarrDbContext db, IEventHub eventHub, IReadOnlyCollection<int> ids, bool unmonitor, CancellationToken ct)
    {
        var files = await db.EpisodeFiles.AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.SeriesId, f.RelativePath, f.QualityId })
            .ToListAsync(ct);
        var seriesIds = files.Select(f => f.SeriesId).Distinct().ToList();
        var folders = await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Path, ct);

        var errors = new List<string>();
        var history = new List<HistoryEntry>();
        var touched = new HashSet<int>();
        foreach (var file in files)
        {
            try
            {
                LibraryFiles.DeleteVideo(folders[file.SeriesId], file.RelativePath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{file.RelativePath}: {ex.Message}");
                continue;
            }

            var fileId = file.Id;
            await db.Episodes.Where(e => e.EpisodeFileId == fileId).ExecuteUpdateAsync(s =>
            {
                s.SetProperty(e => e.EpisodeFileId, (int?)null);
                if (unmonitor) s.SetProperty(e => e.Monitored, false);
            }, ct);
            // Subtitle rows cascade with the file.
            await db.EpisodeFiles.Where(f => f.Id == fileId).ExecuteDeleteAsync(ct);

            var entry = new HistoryEntry
            {
                EventType = HistoryEventType.FileDeleted,
                SourceTitle = file.RelativePath,
                SeriesId = file.SeriesId,
                QualityId = file.QualityId,
                Message = unmonitor ? "Deleted manually; episodes unmonitored" : "Deleted manually",
            };
            db.History.Add(entry);
            history.Add(entry);
            touched.Add(file.SeriesId);
        }

        await db.SaveChangesAsync(ct);
        foreach (var entry in history) await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        foreach (var id in touched) await eventHub.PublishAsync(EventNames.SeriesUpdated, new { Id = id }, ct);
        return new BulkResult(history.Count, errors);
    }
}
