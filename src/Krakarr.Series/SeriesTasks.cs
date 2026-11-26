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
    public string Name => "RSS sync (series)";
    public TimeSpan Interval => TimeSpan.FromMinutes(new AutomationSettings().RssSyncIntervalMinutes);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(2);

    public async ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync<AutomationSettings>(cancellationToken)).RssSyncInterval;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Only the fields needed to match releases; a series (with all its episodes) is loaded only when it has candidates.
        var monitored = await db.Series.AsNoTracking().Where(s => s.Monitored)
            .Select(s => new { s.Id, s.Title, s.MetadataTitle, s.TvdbId, s.TmdbId }).ToListAsync(cancellationToken);
        if (monitored.Count == 0) return;

        var feed = await indexerSearch.SearchAsync(new SearchCriteria { Type = SearchType.Tv, Rss = true }, ct: cancellationToken);
        logger.LogInformation("RSS sync (series): {Count} release(s) from indexers", feed.Releases.Count);

        // Group releases by the series they name, so each series is only loaded when it has candidates.
        var byTitle = feed.Releases
            .Select(r => (Release: r, Parsed: ReleaseParser.Parse(r.Title)))
            .Where(x => x.Parsed.SeasonNumber is not null)
            .GroupBy(x => ReleaseParser.NormalizeTitle(x.Parsed.Title))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Release).ToList());
        var alternates = await AlternateTitleMatchesAsync(db, byTitle.Keys, cancellationToken);

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var languageSettings = await settings.GetAsync<LanguageSettings>(cancellationToken);
        var busy = await seriesService.ActiveEpisodeIdsAsync(cancellationToken);
        foreach (var info in monitored)
        {
            var candidates = new[] { SeriesNaming.NormalizedTitle(info.Title), SeriesNaming.NormalizedTitle(info.MetadataTitle) }
                .Concat(alternates[info.Id])
                .Where(t => t.Length > 0)
                .Distinct()
                .SelectMany(t => byTitle.GetValueOrDefault(t) ?? [])
                .Concat(feed.Releases.Where(r => (info.TvdbId is not null && r.TvdbId == info.TvdbId) || (info.TmdbId is not null && r.TmdbId == info.TmdbId)))
                .Distinct()
                .ToList();
            if (candidates.Count == 0) continue;

            // Every operation below saves its own changes, so drop the previous series from the change tracker
            // instead of accumulating the whole library in this scope's DbContext.
            db.ChangeTracker.Clear();
            var series = await seriesService.LoadAsync(info.Id, cancellationToken);
            if (series?.QualityProfile is null) continue;

            var decisions = EpisodeDecisionEngine.Evaluate(series, series.QualityProfile, series.Episodes, candidates, null, requireMonitored: true, today,
                languageSettings.For(series.Languages));
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

    /// <summary>Series ids whose alternate titles equal one of the normalised release titles (a slim lookup, not the library).</summary>
    internal static async Task<ILookup<int, string>> AlternateTitleMatchesAsync(KrakarrDbContext db, IEnumerable<string> normalizedTitles, CancellationToken ct)
    {
        var titles = normalizedTitles.Where(t => t.Length > 0).ToList();
        if (titles.Count == 0) return Array.Empty<int>().ToLookup(x => x, _ => string.Empty);
        var rows = await db.AlternateTitles.AsNoTracking()
            .Where(t => t.SeriesId != null && titles.Contains(t.NormalizedTitle))
            .Select(t => new { SeriesId = t.SeriesId!.Value, t.NormalizedTitle })
            .ToListAsync(cancellationToken: ct);
        return rows.ToLookup(r => r.SeriesId, r => r.NormalizedTitle);
    }
}

/// <summary>Refreshes series metadata and episode lists from the configured source (TMDB or TheTVDB) and rescans series folders twice a day.</summary>
public sealed class SeriesRefreshTask(KrakarrDbContext db, SeriesService seriesService, ILogger<SeriesRefreshTask> logger) : IScheduledTask
{
    public string Name => "Refresh series metadata";
    public TimeSpan Interval => TimeSpan.FromHours(12);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        foreach (var id in await db.Series.AsNoTracking().Select(s => s.Id).ToListAsync(cancellationToken))
        {
            db.ChangeTracker.Clear(); // RefreshAsync saves; don't keep every refreshed series (and its episodes) tracked
            var series = await seriesService.LoadAsync(id, cancellationToken);
            if (series is null) continue;
            if (failures >= 3)
            {
                await seriesService.RescanDiskAsync(series, cancellationToken);
                continue;
            }

            try
            {
                await seriesService.RefreshAsync(series, cancellationToken);
                failures = 0;
            }
            catch (MetadataException ex)
            {
                logger.LogWarning("Could not refresh {Title}: {Message}", series.Title, ex.Message);
                failures++; // likely a missing key or the source is down; don't spam metadata source, still rescan disk
            }
        }
    }
}
