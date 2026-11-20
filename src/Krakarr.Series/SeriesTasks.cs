using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Indexers;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

/// <summary>Periodically pulls the latest TV releases and grabs wanted episodes of monitored series.</summary>
public sealed class SeriesRssSyncTask(
    KrakarrDbContext db,
    IIndexerSearchService indexerSearch,
    SeriesService seriesService,
    ISettingsService settings,
    TimeProvider time,
    ILogger<SeriesRssSyncTask> logger) : IScheduledTask
{
    private static DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public string Name => "RSS sync (series)";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(2);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var automation = await settings.GetAsync<AutomationSettings>(cancellationToken);
        if (automation.RssSyncIntervalMinutes <= 0 || time.GetUtcNow() - _lastRun < TimeSpan.FromMinutes(automation.RssSyncIntervalMinutes))
        {
            return;
        }

        _lastRun = time.GetUtcNow();
        var seriesIds = await db.Series.Where(s => s.Monitored).Select(s => s.Id).ToListAsync(cancellationToken);
        if (seriesIds.Count == 0) return;

        var feed = await indexerSearch.SearchAsync(new SearchCriteria { Type = SearchType.Tv, Rss = true }, ct: cancellationToken);
        logger.LogInformation("RSS sync (series): {Count} release(s) from indexers", feed.Releases.Count);

        // Group releases by the series they name, so each series is only loaded when it has candidates.
        var byTitle = feed.Releases
            .Select(r => (Release: r, Parsed: ReleaseParser.Parse(r.Title)))
            .Where(x => x.Parsed.SeasonNumber is not null)
            .GroupBy(x => ReleaseParser.NormalizeTitle(x.Parsed.Title))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Release).ToList());

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var busy = await seriesService.ActiveEpisodeIdsAsync(cancellationToken);
        foreach (var id in seriesIds)
        {
            var series = await seriesService.LoadAsync(id, cancellationToken);
            if (series?.QualityProfile is null) continue;
            var candidates = (byTitle.GetValueOrDefault(SeriesNaming.NormalizedTitle(series.Title)) ?? [])
                .Concat(feed.Releases.Where(r => (series.TvdbId is not null && r.TvdbId == series.TvdbId) || (series.TmdbId is not null && r.TmdbId == series.TmdbId)))
                .Distinct()
                .ToList();
            if (candidates.Count == 0) continue;

            var decisions = EpisodeDecisionEngine.Evaluate(series, series.QualityProfile, series.Episodes, candidates, null, requireMonitored: true, today);
            foreach (var decision in EpisodeDecisionEngine.SelectGrabs(decisions, busy))
            {
                try
                {
                    await seriesService.GrabAsync(series, decision.Release, decision.Episodes, cancellationToken);
                    foreach (var e in decision.Episodes) busy.Add(e.Id);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
                {
                    logger.LogWarning("Could not grab {Release}: {Message}", decision.Release.Title, ex.Message);
                }
            }
        }
    }
}

/// <summary>Refreshes series metadata and episode lists from the configured source (TMDB or TheTVDB) twice a day.</summary>
public sealed class SeriesRefreshTask(KrakarrDbContext db, SeriesService seriesService, ILogger<SeriesRefreshTask> logger) : IScheduledTask
{
    public string Name => "Refresh series metadata";
    public TimeSpan Interval => TimeSpan.FromHours(12);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var id in await db.Series.Select(s => s.Id).ToListAsync(cancellationToken))
        {
            var series = await seriesService.LoadAsync(id, cancellationToken);
            if (series is null) continue;
            try
            {
                await seriesService.RefreshAsync(series, cancellationToken);
                failures = 0;
            }
            catch (MetadataException ex)
            {
                logger.LogWarning("Could not refresh {Title}: {Message}", series.Title, ex.Message);
                if (++failures >= 3) return; // likely a missing key or the source is down; don't spam
            }
        }
    }
}
