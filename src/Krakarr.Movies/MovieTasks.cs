using Krakarr.Core.Metadata;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Indexers;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Movies;

/// <summary>Periodically pulls the latest releases from indexers and grabs matches for wanted movies.</summary>
public sealed class MovieRssSyncTask(
    KrakarrDbContext db,
    IIndexerSearchService indexerSearch,
    MovieService movies,
    ISettingsService settings,
    ILogger<MovieRssSyncTask> logger) : IScheduledTask
{
    public string Name => "RSS sync (movies)";
    public TimeSpan Interval => TimeSpan.FromMinutes(new AutomationSettings().RssSyncIntervalMinutes);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(2);

    public async ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync<AutomationSettings>(cancellationToken)).RssSyncInterval;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Only the fields needed to match releases; a movie is loaded only when the feed has candidates for it.
        var wanted = await db.Movies.AsNoTracking().Where(m => m.Monitored)
            .Select(m => new { m.Id, m.Title, m.OriginalTitle, m.TmdbId, m.ImdbId })
            .ToListAsync(cancellationToken);
        if (wanted.Count == 0)
        {
            return;
        }

        var feed = await indexerSearch.SearchAsync(new SearchCriteria { Type = SearchType.Movie, Rss = true }, ct: cancellationToken);
        logger.LogInformation("RSS sync: {Count} release(s) from indexers", feed.Releases.Count);
        var languageSettings = await settings.GetAsync<LanguageSettings>(cancellationToken);

        var byTitle = feed.Releases
            .GroupBy(r => ReleaseParser.NormalizeTitle(ReleaseParser.Parse(r.Title).Title))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList());
        var alternates = await AlternateTitleMatchesAsync(db, byTitle.Keys, cancellationToken);

        foreach (var info in wanted)
        {
            var imdb = info.ImdbId is { Length: > 2 } i && int.TryParse(i.AsSpan(2), out var n) ? n : (int?)null;
            var candidates = new[] { ReleaseParser.NormalizeTitle(info.Title), ReleaseParser.NormalizeTitle(info.OriginalTitle) }
                .Concat(alternates[info.Id])
                .Distinct()
                .SelectMany(t => byTitle.GetValueOrDefault(t) ?? [])
                .Concat(feed.Releases.Where(r => r.TmdbId == info.TmdbId || (imdb is not null && r.ImdbId == imdb)))
                .Distinct()
                .ToList();
            if (candidates.Count == 0) continue;

            // GrabAsync saves its own changes; keep only the current movie tracked.
            db.ChangeTracker.Clear();
            var movie = await db.Movies.Include(m => m.QualityProfile).Include(m => m.MovieFile).Include(m => m.AlternateTitles)
                .AsSplitQuery()
                .FirstOrDefaultAsync(m => m.Id == info.Id, cancellationToken);
            if (movie?.QualityProfile is null || !MovieReleases.IsAvailable(movie, MovieService.Today)
                || await movies.HasActiveDownloadAsync(movie.Id, cancellationToken))
            {
                continue;
            }

            var existing = movie.MovieFile is null ? null : Qualities.FindById(movie.MovieFile.QualityId);
            var best = MovieDecisionEngine.Evaluate(movie, movie.QualityProfile, existing, candidates, languageSettings.For(movie.Languages)).FirstOrDefault(d => d.Approved);
            if (best is not null)
            {
                await movies.GrabAsync(movie, best.Release, cancellationToken);
            }
        }
    }

    /// <summary>Movie ids whose alternate titles equal one of the normalised release titles (a slim lookup, not the library).</summary>
    internal static async Task<ILookup<int, string>> AlternateTitleMatchesAsync(KrakarrDbContext db, IEnumerable<string> normalizedTitles, CancellationToken ct)
    {
        var titles = normalizedTitles.ToList();
        if (titles.Count == 0) return Array.Empty<int>().ToLookup(x => x, _ => string.Empty);
        var rows = await db.AlternateTitles.AsNoTracking()
            .Where(t => t.MovieId != null && titles.Contains(t.NormalizedTitle))
            .Select(t => new { MovieId = t.MovieId!.Value, t.NormalizedTitle })
            .ToListAsync(ct);
        return rows.ToLookup(r => r.MovieId, r => r.NormalizedTitle);
    }
}

/// <summary>Refreshes movie metadata from TMDB and rescans movie folders once a day.</summary>
public sealed class MovieRefreshTask(KrakarrDbContext db, MovieService movies, ILogger<MovieRefreshTask> logger) : IScheduledTask
{
    public string Name => "Refresh movie metadata";
    public TimeSpan Interval => TimeSpan.FromHours(24);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await MovieService.PruneCollectionsAsync(db, cancellationToken);
        var skipMetadata = false;
        foreach (var id in await db.Movies.AsNoTracking().Select(m => m.Id).ToListAsync(cancellationToken))
        {
            db.ChangeTracker.Clear(); // RefreshAsync saves; keep only the current movie tracked
            var movie = await db.Movies.Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (movie is null) continue;
            if (skipMetadata)
            {
                await movies.RescanDiskAsync(movie, cancellationToken);
                continue;
            }

            try
            {
                await movies.RefreshAsync(movie, cancellationToken);
            }
            catch (MetadataException ex)
            {
                logger.LogWarning("Could not refresh {Title}: {Message}", movie.Title, ex.Message);
                skipMetadata = true; // likely missing API key; don't spam TMDB, still rescan disk
            }
        }
    }
}
