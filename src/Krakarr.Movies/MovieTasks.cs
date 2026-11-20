using Krakarr.Core.Metadata;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Indexers;
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
    private static DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public string Name => "RSS sync (movies)";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(2);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var automation = await settings.GetAsync<AutomationSettings>(cancellationToken);
        if (automation.RssSyncIntervalMinutes <= 0
            || DateTimeOffset.UtcNow - _lastRun < TimeSpan.FromMinutes(automation.RssSyncIntervalMinutes))
        {
            return;
        }

        _lastRun = DateTimeOffset.UtcNow;

        var wanted = await db.Movies.Include(m => m.QualityProfile).Include(m => m.MovieFile)
            .Where(m => m.Monitored)
            .ToListAsync(cancellationToken);
        if (wanted.Count == 0)
        {
            return;
        }

        var feed = await indexerSearch.SearchAsync(new SearchCriteria { Type = SearchType.Movie, Rss = true }, ct: cancellationToken);
        logger.LogInformation("RSS sync: {Count} release(s) from indexers", feed.Releases.Count);

        foreach (var movie in wanted)
        {
            if (movie.QualityProfile is null || !MovieReleases.IsAvailable(movie, MovieService.Today)
                || await movies.HasActiveDownloadAsync(movie.Id, cancellationToken))
            {
                continue;
            }

            var existing = movie.MovieFile is null ? null : Qualities.FindById(movie.MovieFile.QualityId);
            var best = MovieDecisionEngine.Evaluate(movie, movie.QualityProfile, existing, feed.Releases).FirstOrDefault(d => d.Approved);
            if (best is not null)
            {
                await movies.GrabAsync(movie, best.Release, cancellationToken);
            }
        }
    }
}

/// <summary>Refreshes movie metadata from TMDB once a day.</summary>
public sealed class MovieRefreshTask(KrakarrDbContext db, MovieService movies, ILogger<MovieRefreshTask> logger) : IScheduledTask
{
    public string Name => "Refresh movie metadata";
    public TimeSpan Interval => TimeSpan.FromHours(24);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var id in await db.Movies.AsNoTracking().Select(m => m.Id).ToListAsync(cancellationToken))
        {
            db.ChangeTracker.Clear(); // RefreshAsync saves; keep only the current movie tracked
            var movie = await db.Movies.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (movie is null) continue;
            try
            {
                await movies.RefreshAsync(movie, cancellationToken);
            }
            catch (MetadataException ex)
            {
                logger.LogWarning("Could not refresh {Title}: {Message}", movie.Title, ex.Message);
                return; // likely missing API key; don't spam
            }
        }
    }
}
