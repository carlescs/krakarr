using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

public sealed record AddSeriesRequest(
    int TvdbId, int QualityProfileId, string RootFolderPath, MonitorMode Monitor = MonitorMode.All, bool SeasonFolder = true, bool SearchForMissing = true);

public sealed class SeriesService(
    KrakarrDbContext db,
    ITvdbClient tvdb,
    ISettingsService settings,
    IIndexerSearchService indexerSearch,
    IDownloadService downloads,
    IEventHub eventHub,
    TimeProvider time,
    ILogger<SeriesService> logger)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Loads a series with everything the decision engine and import need.</summary>
    public Task<Core.Domain.Series?> LoadAsync(int id, CancellationToken ct = default) =>
        db.Series
            .Include(s => s.QualityProfile)
            .Include(s => s.Seasons)
            .Include(s => s.Episodes).ThenInclude(e => e.EpisodeFile)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    // ---- Library -----------------------------------------------------------------------------

    public async Task<Core.Domain.Series> AddAsync(AddSeriesRequest request, CancellationToken ct = default)
    {
        if (await db.Series.AnyAsync(s => s.TvdbId == request.TvdbId, ct))
            throw new InvalidOperationException("Series is already in the library");
        if (!await db.QualityProfiles.AnyAsync(p => p.Id == request.QualityProfileId, ct))
            throw new InvalidOperationException("Quality profile does not exist");
        if (string.IsNullOrWhiteSpace(request.RootFolderPath))
            throw new InvalidOperationException("A root folder is required");

        var info = await tvdb.GetSeriesAsync(request.TvdbId, ct);
        var media = await settings.GetAsync<MediaManagementSettings>(ct);

        var series = new Core.Domain.Series
        {
            TvdbId = info.TvdbId,
            Title = info.Title,
            Path = string.Empty,
            QualityProfileId = request.QualityProfileId,
            SeasonFolder = request.SeasonFolder,
            Monitored = request.Monitor != MonitorMode.None,
        };
        ApplyInfo(series, info);
        series.Path = Path.Combine(request.RootFolderPath, SeriesNaming.BuildSeriesFolder(series, media.SeriesFolderFormat));

        foreach (var e in info.Episodes)
        {
            series.Episodes.Add(new Episode { TvdbId = e.TvdbId, SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, Monitored = false });
            Update(series.Episodes[^1], e);
        }

        foreach (var number in series.Episodes.Select(e => e.SeasonNumber).Distinct().Order())
        {
            series.Seasons.Add(new Season { SeasonNumber = number, Monitored = false });
        }

        ApplyMonitorMode(series, request.Monitor, Today);

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added series {Title} ({Episodes} episodes)", series.Title, series.Episodes.Count);
        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);
        return series;
    }

    /// <summary>Sets season/episode monitoring for a newly added series. Specials are never monitored automatically.</summary>
    public static void ApplyMonitorMode(Core.Domain.Series series, MonitorMode mode, DateOnly today)
    {
        var regularSeasons = series.Seasons.Where(s => s.SeasonNumber > 0).Select(s => s.SeasonNumber).ToList();
        var first = regularSeasons.Count > 0 ? regularSeasons.Min() : 1;
        var latest = regularSeasons.Count > 0 ? regularSeasons.Max() : 1;

        foreach (var e in series.Episodes)
        {
            e.Monitored = e.SeasonNumber > 0 && mode switch
            {
                MonitorMode.All or MonitorMode.Missing => true,
                MonitorMode.Future => e.AirDate is null || e.AirDate > today,
                MonitorMode.FirstSeason => e.SeasonNumber == first,
                MonitorMode.LatestSeason => e.SeasonNumber == latest,
                _ => false,
            };
        }

        foreach (var season in series.Seasons)
        {
            season.Monitored = season.SeasonNumber > 0 && mode switch
            {
                MonitorMode.All or MonitorMode.Missing => true,
                MonitorMode.Future => series.Episodes.Any(e => e.SeasonNumber == season.SeasonNumber && e.Monitored) || season.SeasonNumber == latest,
                MonitorMode.FirstSeason => season.SeasonNumber == first,
                MonitorMode.LatestSeason => season.SeasonNumber == latest,
                _ => false,
            };
        }
    }

    /// <summary>Re-reads metadata and episodes from TheTVDB. New episodes inherit their season's monitoring.</summary>
    public async Task RefreshAsync(Core.Domain.Series series, CancellationToken ct = default)
    {
        var info = await tvdb.GetSeriesAsync(series.TvdbId, ct);
        ApplyInfo(series, info);

        var byTvdb = series.Episodes.Where(e => e.TvdbId > 0).ToDictionary(e => e.TvdbId);
        var seen = new HashSet<Episode>();
        foreach (var e in info.Episodes)
        {
            var episode = (e.TvdbId > 0 ? byTvdb.GetValueOrDefault(e.TvdbId) : null)
                ?? series.Episodes.FirstOrDefault(x => x.SeasonNumber == e.SeasonNumber && x.EpisodeNumber == e.EpisodeNumber && !seen.Contains(x));
            if (episode is null)
            {
                var season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == e.SeasonNumber);
                if (season is null)
                {
                    season = new Season { SeasonNumber = e.SeasonNumber, Monitored = series.Monitored && e.SeasonNumber > 0 };
                    series.Seasons.Add(season);
                }

                episode = new Episode { TvdbId = e.TvdbId, Monitored = series.Monitored && season.Monitored && e.SeasonNumber > 0 };
                series.Episodes.Add(episode);
            }

            episode.TvdbId = e.TvdbId;
            episode.SeasonNumber = e.SeasonNumber;
            episode.EpisodeNumber = e.EpisodeNumber;
            Update(episode, e);
            seen.Add(episode);
        }

        // Episodes removed upstream are dropped unless we hold a file for them.
        foreach (var gone in series.Episodes.Where(e => !seen.Contains(e) && e.EpisodeFileId is null).ToList())
        {
            series.Episodes.Remove(gone);
            db.Episodes.Remove(gone);
        }

        series.LastRefreshed = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);
    }

    private static void ApplyInfo(Core.Domain.Series s, SeriesInfo info)
    {
        s.Title = info.Title;
        s.SortTitle = SortTitle(info.Title);
        s.Year = info.Year;
        s.Overview = info.Overview;
        s.PosterUrl = info.PosterUrl ?? s.PosterUrl;
        s.FanartUrl = info.FanartUrl ?? s.FanartUrl;
        s.Network = info.Network;
        s.Runtime = info.Runtime;
        s.ImdbId = info.ImdbId ?? s.ImdbId;
        s.FirstAired = info.FirstAired;
        s.Status = info.Status?.ToLowerInvariant() switch
        {
            "ended" => SeriesStatus.Ended,
            "upcoming" => SeriesStatus.Upcoming,
            _ => SeriesStatus.Continuing,
        };
    }

    private static void Update(Episode episode, EpisodeInfo e)
    {
        episode.AbsoluteNumber = e.AbsoluteNumber;
        episode.Title = e.Title;
        episode.Overview = e.Overview;
        episode.AirDate = e.AirDate;
        episode.Runtime = e.Runtime;
    }

    private static string SortTitle(string title)
    {
        var t = title.Trim();
        foreach (var article in new[] { "The ", "A ", "An " })
        {
            if (t.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return t[article.Length..].ToLowerInvariant();
        }

        return t.ToLowerInvariant();
    }

    // ---- Search & grab -----------------------------------------------------------------------

    /// <summary>Searches indexers for a season or a single episode and evaluates every release.</summary>
    public async Task<(IReadOnlyList<EpisodeDecision> Decisions, IReadOnlyList<IndexerError> Errors)> SearchAsync(
        Core.Domain.Series series, int? seasonNumber, Episode? episode, bool requireMonitored, CancellationToken ct = default)
    {
        var season = episode?.SeasonNumber ?? seasonNumber;
        var result = await indexerSearch.SearchAsync(new SearchCriteria
        {
            Type = SearchType.Tv,
            Query = SeriesNaming.BaseTitle(series.Title),
            TvdbId = series.TvdbId,
            Season = season,
            Episode = episode?.EpisodeNumber,
        }, ct: ct);

        series.LastSearched = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var scope = new SearchScope(season, episode is null ? null : new HashSet<int> { episode.Id });
        var decisions = EpisodeDecisionEngine.Evaluate(series, series.QualityProfile!, series.Episodes, result.Releases, scope, requireMonitored, Today);
        return (decisions, result.Errors);
    }

    /// <summary>
    /// Automatic search: for each season with wanted episodes, search the season (packs and single episodes),
    /// grab the best non-overlapping releases, then search individually for episodes that are still missing.
    /// </summary>
    public async Task<string> AutoSearchAsync(int seriesId, int? seasonNumber, IReadOnlyCollection<int>? episodeIds, CancellationToken ct = default)
    {
        var series = await LoadAsync(seriesId, ct) ?? throw new KeyNotFoundException($"Series {seriesId} not found");
        var profile = series.QualityProfile!;
        var busy = await ActiveEpisodeIdsAsync(ct);
        var wanted = series.Episodes
            .Where(e => EpisodeDecisionEngine.IsWanted(e, profile, Today) && !busy.Contains(e.Id))
            .Where(e => seasonNumber is null || e.SeasonNumber == seasonNumber)
            .Where(e => episodeIds is null || episodeIds.Count == 0 || episodeIds.Contains(e.Id))
            .ToList();

        if (wanted.Count == 0)
        {
            return $"{series.Title}: nothing wanted";
        }

        var grabbed = new List<string>();
        var covered = new HashSet<int>(busy);
        var errors = 0;

        foreach (var season in wanted.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key))
        {
            var seasonWanted = season.ToList();
            // Single episodes are searched directly; several missing episodes trigger a season search first.
            if (seasonWanted.Count > 1 || episodeIds is null or { Count: 0 })
            {
                var (decisions, errs) = await SearchAsync(series, season.Key, null, requireMonitored: true, ct);
                errors += errs.Count;
                await GrabSelectionAsync(series, decisions, covered, grabbed, ct);
            }

            foreach (var episode in seasonWanted.Where(e => !covered.Contains(e.Id)).Take(20))
            {
                var (decisions, errs) = await SearchAsync(series, null, episode, requireMonitored: true, ct);
                errors += errs.Count;
                await GrabSelectionAsync(series, decisions, covered, grabbed, ct);
            }
        }

        var missing = wanted.Count(e => !covered.Contains(e.Id));
        var suffix = errors > 0 ? $" ({errors} indexer error(s))" : string.Empty;
        return grabbed.Count == 0
            ? $"{series.Title}: no acceptable releases for {wanted.Count} wanted episode(s){suffix}"
            : $"{series.Title}: grabbed {grabbed.Count} release(s); {missing} episode(s) still missing{suffix}";
    }

    private async Task GrabSelectionAsync(Core.Domain.Series series, IReadOnlyList<EpisodeDecision> decisions, HashSet<int> covered, List<string> grabbed, CancellationToken ct)
    {
        foreach (var decision in EpisodeDecisionEngine.SelectGrabs(decisions, covered))
        {
            try
            {
                await GrabAsync(series, decision.Release, decision.Episodes, ct);
                grabbed.Add(decision.Release.Title);
                foreach (var e in decision.Episodes) covered.Add(e.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException)
            {
                logger.LogWarning("Could not grab {Release}: {Message}", decision.Release.Title, ex.Message);
            }
        }
    }

    public async Task<Download> GrabAsync(Core.Domain.Series series, ReleaseInfo release, IReadOnlyList<Episode> episodes, CancellationToken ct = default)
    {
        var quality = ReleaseParser.ParseQuality(release.Title);
        var download = await downloads.GrabAsync(release, new AddTorrentOptions
        {
            Category = "tv",
            MediaType = MediaType.Series,
            SeriesId = series.Id,
            EpisodeIds = episodes.Select(e => e.Id).ToList(),
            QualityId = quality.Id,
            DisplayName = release.Title,
        }, ct);

        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.Grabbed,
            SourceTitle = release.Title,
            SeriesId = series.Id,
            QualityId = quality.Id,
            Indexer = release.IndexerName,
            Message = Codes(episodes),
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        logger.LogInformation("Grabbed {Release} for {Series} ({Episodes})", release.Title, series.Title, entry.Message);
        return download;
    }

    /// <summary>Episodes currently being downloaded (not yet imported or failed).</summary>
    public async Task<HashSet<int>> ActiveEpisodeIdsAsync(CancellationToken ct = default)
    {
        var lists = await db.Downloads
            .Where(d => d.SeriesId != null && !d.Imported && d.ImportMessage == null)
            .Select(d => d.EpisodeIds)
            .ToListAsync(ct);
        return lists.SelectMany(l => l).ToHashSet();
    }

    public static string Codes(IEnumerable<Episode> episodes) =>
        string.Join(", ", episodes.GroupBy(e => e.SeasonNumber).OrderBy(g => g.Key)
            .Select(g => SeriesNaming.EpisodeCode(g.Key, g.Select(e => e.EpisodeNumber))));
}
