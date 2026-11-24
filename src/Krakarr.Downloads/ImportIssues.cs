using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Downloads;

/// <summary>A download whose automatic import failed (the dashboard's "imports need attention").</summary>
/// <param name="InClient">Whether the torrent is still in a download client; false once it was removed there.</param>
public sealed record ImportIssue(
    int Id, string InfoHash, string Name, string Client, int? MovieId, int? SeriesId, string Message, DateTimeOffset? CompletedAt, bool InClient);

/// <summary>Lists failed imports and lets the user retry or dismiss them.</summary>
public sealed class ImportIssues(KrakarrDbContext db, ITorrentEngine engine)
{
    /// <summary>Most issues returned at once; there are normally a handful.</summary>
    public const int MaxListed = 200;

    public async Task<IReadOnlyList<ImportIssue>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Downloads.AsNoTracking()
            .Where(d => !d.Imported && d.ImportMessage != null)
            .OrderByDescending(d => d.Id)
            .Take(MaxListed)
            .Select(d => new { d.Id, d.InfoHash, d.Name, d.Client, d.MovieId, d.SeriesId, d.ImportMessage, d.CompletedAt })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var present = InClient();
        return rows
            .Select(d => new ImportIssue(d.Id, d.InfoHash, d.Name, d.Client, d.MovieId, d.SeriesId, d.ImportMessage!, d.CompletedAt, present.Contains(d.InfoHash)))
            .ToList();
    }

    /// <summary>Clears the failure so the import task tries again. Returns false when there is no such issue.</summary>
    public async Task<bool> RetryAsync(int id, CancellationToken ct = default)
    {
        var row = await FindAsync(id, ct);
        if (row is null) return false;
        if (row.CompletedAt is null || !InClient().Contains(row.InfoHash))
        {
            throw new InvalidOperationException("Only completed downloads that are still in the download client can be imported again.");
        }

        row.ImportMessage = null;
        await SaveAsync(ct);
        return true;
    }

    /// <summary>
    /// Stops tracking the failed import: a download that left its client is forgotten; one still in the client is kept
    /// as a plain torrent, no longer linked to the movie or series. Returns false when there is no such issue.
    /// </summary>
    public async Task<bool> DismissAsync(int id, CancellationToken ct = default)
    {
        var row = await FindAsync(id, ct);
        if (row is null) return false;
        if (InClient().Contains(row.InfoHash))
        {
            row.MovieId = null;
            row.SeriesId = null;
            row.MediaType = null;
            row.EpisodeIds = [];
            row.ImportMessage = null;
        }
        else
        {
            db.Downloads.Remove(row);
        }

        await SaveAsync(ct);
        return true;
    }

    private Task<Download?> FindAsync(int id, CancellationToken ct) =>
        db.Downloads.FirstOrDefaultAsync(d => d.Id == id && !d.Imported && d.ImportMessage != null, ct);

    private HashSet<string> InClient() =>
        engine.GetStatuses().Select(s => s.InfoHash).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The download feed picks the change up on its next tick.
    private Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
