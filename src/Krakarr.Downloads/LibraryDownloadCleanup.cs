using System.Linq.Expressions;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Downloads;

/// <summary>Removes the downloads of a movie or series that is being deleted from the library.</summary>
public static class LibraryDownloadCleanup
{
    /// <summary>
    /// Removes every download of the title that hasn't been imported yet (queued, downloading, paused, completed but
    /// not imported, failed) from its download client, deleting the downloaded data too. Imported downloads are left
    /// alone (they may still be seeding); the caller unlinks them.
    /// </summary>
    /// <returns>An error message when a download could not be removed, otherwise null.</returns>
    public static async Task<string?> RemovePendingAsync(
        KrakarrDbContext db, ITorrentEngine engine, Expression<Func<Download, bool>> ofTitle, CancellationToken ct)
    {
        var pending = await db.Downloads.AsNoTracking().Where(ofTitle).Where(d => !d.Imported)
            .Select(d => new { d.InfoHash, d.Name }).ToListAsync(ct);
        foreach (var download in pending)
        {
            try
            {
                await engine.RemoveAsync(download.InfoHash, deleteData: true);
            }
            catch (KeyNotFoundException)
            {
                // Already gone from the client: just drop the row.
                await db.Downloads.Where(d => d.InfoHash == download.InfoHash).ExecuteDeleteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"Could not remove the download {download.Name}: {ex.Message}";
            }
        }

        return null;
    }
}
