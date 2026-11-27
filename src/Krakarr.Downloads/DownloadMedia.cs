using System.Globalization;
using System.Text;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Downloads;

/// <summary>The library item a download belongs to, so the downloads page can name and link it.</summary>
/// <param name="Id">The download's id (<see cref="TorrentStatus.Id"/>, also the import issue id).</param>
/// <param name="Title">The movie or series title.</param>
/// <param name="Year">Release year (null when unknown).</param>
/// <param name="Episodes">Episode codes the download is expected to contain, e.g. "S01E01–E03" (series only).</param>
public sealed record DownloadMediaInfo(int Id, int? MovieId, int? SeriesId, string Title, int? Year, string? Episodes);

/// <summary>Resolves the library items of the downloads in the clients and of failed imports.</summary>
public sealed class DownloadMedia(KrakarrDbContext db, ITorrentEngine engine)
{
    /// <summary>Most episodes named in one code before it is shortened ("S01E01–E10 +3 more").</summary>
    private const int MaxEpisodeRuns = 6;

    public async Task<IReadOnlyList<DownloadMediaInfo>> ListAsync(CancellationToken ct = default)
    {
        var inClient = engine.GetStatuses().Where(s => s.MovieId != null || s.SeriesId != null).Select(s => s.Id).ToHashSet();
        var rows = await db.Downloads.AsNoTracking()
            .Where(d => d.MovieId != null || d.SeriesId != null)
            .Where(d => inClient.Contains(d.Id) || (!d.Imported && d.ImportMessage != null))
            .OrderByDescending(d => d.Id)
            .Take(ImportIssues.MaxListed + inClient.Count)
            .Select(d => new { d.Id, d.MovieId, d.SeriesId, d.EpisodeIds })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var movieIds = rows.Where(r => r.MovieId != null).Select(r => r.MovieId!.Value).Distinct().ToList();
        var seriesIds = rows.Where(r => r.SeriesId != null).Select(r => r.SeriesId!.Value).Distinct().ToList();
        var episodeIds = rows.SelectMany(r => r.EpisodeIds).Distinct().ToList();

        var movies = movieIds.Count == 0 ? [] : await db.Movies.AsNoTracking().Where(m => movieIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Title, m.Year }).ToDictionaryAsync(m => m.Id, ct);
        var series = seriesIds.Count == 0 ? [] : await db.Series.AsNoTracking().Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.Year }).ToDictionaryAsync(s => s.Id, ct);
        var episodes = episodeIds.Count == 0 ? [] : await db.Episodes.AsNoTracking().Where(e => episodeIds.Contains(e.Id))
            .Select(e => new { e.Id, e.SeasonNumber, e.EpisodeNumber }).ToDictionaryAsync(e => e.Id, e => (e.SeasonNumber, e.EpisodeNumber), ct);

        var result = new List<DownloadMediaInfo>(rows.Count);
        foreach (var r in rows)
        {
            if (r.MovieId is { } movieId && movies.TryGetValue(movieId, out var m))
            {
                result.Add(new DownloadMediaInfo(r.Id, movieId, null, m.Title, m.Year > 0 ? m.Year : null, null));
            }
            else if (r.SeriesId is { } seriesId && series.TryGetValue(seriesId, out var s))
            {
                var codes = EpisodeCodes(r.EpisodeIds.Where(episodes.ContainsKey).Select(id => episodes[id]));
                result.Add(new DownloadMediaInfo(r.Id, null, seriesId, s.Title, s.Year > 0 ? s.Year : null, codes));
            }
        }

        return result;
    }

    /// <summary>
    /// Compact episode codes: consecutive episodes collapse into a range, seasons are separated by commas
    /// ("S01E01–E03 E05, S02E01"). Long lists are cut after a few runs.
    /// </summary>
    public static string? EpisodeCodes(IEnumerable<(int Season, int Episode)> episodes)
    {
        var bySeason = episodes.Distinct().GroupBy(e => e.Season).OrderBy(g => g.Key).ToList();
        if (bySeason.Count == 0) return null;

        var sb = new StringBuilder();
        var runs = 0;
        var skipped = 0;
        foreach (var season in bySeason)
        {
            var numbers = season.Select(e => e.Episode).Order().ToList();
            var first = true;
            for (var i = 0; i < numbers.Count;)
            {
                var j = i;
                while (j + 1 < numbers.Count && numbers[j + 1] == numbers[j] + 1) j++;
                if (runs >= MaxEpisodeRuns)
                {
                    skipped += j - i + 1;
                }
                else
                {
                    if (first)
                    {
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(CultureInfo.InvariantCulture, $"S{season.Key:00}");
                        first = false;
                    }
                    else
                    {
                        sb.Append(' ');
                    }

                    sb.Append(CultureInfo.InvariantCulture, $"E{numbers[i]:00}");
                    if (j > i) sb.Append(CultureInfo.InvariantCulture, $"–E{numbers[j]:00}");
                    runs++;
                }

                i = j + 1;
            }
        }

        if (skipped > 0) sb.Append(CultureInfo.InvariantCulture, $" +{skipped} more");
        return sb.ToString();
    }
}
