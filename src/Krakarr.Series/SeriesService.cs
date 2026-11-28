using System.Runtime.CompilerServices;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Metadata;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Krakarr.Subtitles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

/// <param name="ExistingPath">Use this existing folder as-is (library import) instead of building one from the naming format.</param>
/// <param name="Title">Title to use instead of the metadata one, e.g. to tell apart two series with the same name; null or blank = the metadata title.</param>
public sealed record AddSeriesRequest(
    int? TmdbId, int? TvdbId, int QualityProfileId, string RootFolderPath, MonitorMode Monitor = MonitorMode.All, bool SeasonFolder = true, bool SearchForMissing = true,
    string? ExistingPath = null, IReadOnlyList<string>? Languages = null, string? Title = null);

public sealed class SeriesService(
    KrakarrDbContext db,
    ISeriesMetadata metadata,
    ISettingsService settings,
    IIndexerSearchService indexerSearch,
    IDownloadService downloads,
    IEventHub eventHub,
    TimeProvider time,
    IXemClient xem,
    ILogger<SeriesService> logger,
    Krakarr.Core.Push.IPushNotifier? push = null)
{
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Loads a series with everything the decision engine and import need.</summary>
    public Task<Core.Domain.Series?> LoadAsync(int id, CancellationToken ct = default) =>
        db.Series
            .Include(s => s.QualityProfile)
            .Include(s => s.Seasons)
            .Include(s => s.Episodes).ThenInclude(e => e.EpisodeFile)
            .Include(s => s.AlternateTitles)
            .AsSplitQuery()
            .FirstOrDefaultAsync(s => s.Id == id, ct);

    // ---- Library -----------------------------------------------------------------------------

    public async Task<Core.Domain.Series> AddAsync(AddSeriesRequest request, CancellationToken ct = default)
    {
        var ids = new SeriesIds(request.TmdbId, request.TvdbId);
        if (ids.IsEmpty)
            throw new InvalidOperationException("A TMDB or TheTVDB id is required");
        if (await InLibraryAsync(ids, null, ct))
            throw new InvalidOperationException("Series is already in the library");
        if (!await db.QualityProfiles.AnyAsync(p => p.Id == request.QualityProfileId, ct))
            throw new InvalidOperationException("Quality profile does not exist");
        if (string.IsNullOrWhiteSpace(request.RootFolderPath) && string.IsNullOrWhiteSpace(request.ExistingPath))
            throw new InvalidOperationException("A root folder is required");
        if (request.Title?.Trim().Length > MaxTitleLength)
            throw new InvalidOperationException($"Title must be at most {MaxTitleLength} characters");

        var info = await metadata.GetSeriesAsync(ids, ct);
        if (await InLibraryAsync(new SeriesIds(info.TmdbId, info.TvdbId), null, ct))
            throw new InvalidOperationException("Series is already in the library");
        var media = await settings.GetAsync<MediaManagementSettings>(ct);

        var series = new Core.Domain.Series
        {
            Title = info.Title,
            Path = string.Empty,
            QualityProfileId = request.QualityProfileId,
            Languages = request.Languages is null ? null : MediaLanguages.Clean(request.Languages),
            SeasonFolder = request.SeasonFolder,
            Monitored = request.Monitor != MonitorMode.None,
        };
        ApplyInfo(series, info);
        if (!string.IsNullOrWhiteSpace(request.Title)) SetTitle(series, request.Title);
        var roots = await db.RootFolders.AsNoTracking().Where(r => r.MediaType == MediaType.Series).Select(r => r.Path).ToListAsync(ct);
        series.Path = LibraryPaths.ResolveItemFolder(roots, request.RootFolderPath, request.ExistingPath,
            () => SeriesNaming.BuildSeriesFolder(series, media.SeriesFolderFormat));
        if (await db.Series.AnyAsync(s => s.Path == series.Path, ct))
            throw new InvalidOperationException($"Another series already uses {series.Path}. Give this one a different title, e.g. \"{series.Title} ({series.Year})\".");

        foreach (var e in info.Episodes)
        {
            series.Episodes.Add(new Episode { TmdbId = e.TmdbId, TvdbId = e.TvdbId, SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, Monitored = false });
            Update(series.Episodes[^1], e);
        }

        foreach (var number in series.Episodes.Select(e => e.SeasonNumber).Distinct().Order())
        {
            series.Seasons.Add(new Season { SeasonNumber = number, Monitored = false });
        }

        ApplyMonitorMode(series, request.Monitor, Today);
        await ApplyAlternateTitlesAsync(series, info, ct);
        await ApplySceneMappingAsync(series, ct);

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added series {Title} ({Episodes} episodes)", series.Title, series.Episodes.Count);
        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);
        if (await MediaRequestUpdates.ApproveMatchingAsync(db, MediaType.Series, series.TmdbId, series.TvdbId, series.Id, ct, push) > 0)
        {
            await eventHub.PublishAsync(EventNames.RequestsUpdated, null, ct);
        }

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

    /// <summary>
    /// Re-reads metadata and episodes from the configured source and rescans the series folder on disk.
    /// New episodes inherit their season's monitoring. After switching sources, episodes are matched by
    /// the new source's id when known, else by season/episode number.
    /// </summary>
    public async Task RefreshAsync(Core.Domain.Series series, CancellationToken ct = default)
    {
        HashSet<Episode>? seen = null;
        MetadataException? metadataError = null;
        try
        {
            var info = await metadata.GetSeriesAsync(new SeriesIds(series.TmdbId, series.TvdbId), ct);
            if (await InLibraryAsync(new SeriesIds(info.TmdbId, info.TvdbId), series.Id, ct))
                throw new MetadataException($"{series.Title}: another series in the library has the same TMDB/TheTVDB id");
            ApplyInfo(series, info);

            var byTmdb = series.Episodes.Where(e => e.TmdbId is not null).GroupBy(e => e.TmdbId!.Value).ToDictionary(g => g.Key, g => g.First());
            var byTvdb = series.Episodes.Where(e => e.TvdbId is not null).GroupBy(e => e.TvdbId!.Value).ToDictionary(g => g.Key, g => g.First());
            seen = [];
            foreach (var e in info.Episodes)
            {
                var episode = (e.TmdbId is { } tmdbEp ? byTmdb.GetValueOrDefault(tmdbEp) : null)
                    ?? (e.TvdbId is { } tvdbEp ? byTvdb.GetValueOrDefault(tvdbEp) : null);
                if (episode is not null && seen.Contains(episode)) episode = null;
                episode ??= series.Episodes.FirstOrDefault(x => x.SeasonNumber == e.SeasonNumber && x.EpisodeNumber == e.EpisodeNumber && !seen.Contains(x));
                if (episode is null)
                {
                    var season = series.Seasons.FirstOrDefault(s => s.SeasonNumber == e.SeasonNumber);
                    if (season is null)
                    {
                        season = new Season { SeasonNumber = e.SeasonNumber, Monitored = series.Monitored && e.SeasonNumber > 0 };
                        series.Seasons.Add(season);
                    }

                    episode = new Episode { Monitored = series.Monitored && season.Monitored && e.SeasonNumber > 0 };
                    series.Episodes.Add(episode);
                }

                episode.TmdbId = e.TmdbId ?? episode.TmdbId;
                episode.TvdbId = e.TvdbId ?? episode.TvdbId;
                episode.SeasonNumber = e.SeasonNumber;
                episode.EpisodeNumber = e.EpisodeNumber;
                Update(episode, e);
                seen.Add(episode);
            }

            var titles = db.Entry(series).Collection(x => x.AlternateTitles);
            if (!titles.IsLoaded) await titles.LoadAsync(ct);
            await ApplyAlternateTitlesAsync(series, info, ct);
            await ApplySceneMappingAsync(series, ct);
            series.LastRefreshed = time.GetUtcNow();
        }
        catch (MetadataException ex)
        {
            metadataError = ex;
        }

        await RescanDiskCoreAsync(series, seen, ct);
        if (metadataError is not null) throw metadataError;
    }

    /// <summary>Syncs the series' episode files and sidecar/embedded subtitles with what is currently in <see cref="Core.Domain.Series.Path"/>.</summary>
    public Task RescanDiskAsync(Core.Domain.Series series, CancellationToken ct = default) =>
        RescanDiskCoreAsync(series, seen: null, ct);

    private async Task RescanDiskCoreAsync(Core.Domain.Series series, HashSet<Episode>? seen, CancellationToken ct)
    {
        var existingFiles = await db.EpisodeFiles.Where(f => f.SeriesId == series.Id).ToListAsync(ct);
        var historyEntries = new List<HistoryEntry>();
        var subtitlesChanged = false;
        var survivingFiles = new List<EpisodeFile>();

        foreach (var file in existingFiles)
        {
            var fullPath = Path.Combine(series.Path, file.RelativePath);
            if (!File.Exists(fullPath))
            {
                foreach (var e in series.Episodes.Where(e => e.EpisodeFileId == file.Id))
                {
                    e.EpisodeFileId = null;
                    e.EpisodeFile = null;
                }

                db.EpisodeFiles.Remove(file);
                var deleted = new HistoryEntry
                {
                    EventType = HistoryEventType.FileDeleted,
                    SourceTitle = file.RelativePath,
                    SeriesId = series.Id,
                    QualityId = file.QualityId,
                    Message = "File no longer on disk",
                };
                db.History.Add(deleted);
                historyEntries.Add(deleted);
                subtitlesChanged = true;
                continue;
            }

            if (!series.Episodes.Any(e => e.EpisodeFileId == file.Id))
            {
                db.EpisodeFiles.Remove(file);
                subtitlesChanged = true;
                continue;
            }

            var length = new FileInfo(fullPath).Length;
            if (file.Size != length)
            {
                file.Size = length;
                var parsed = ReleaseParser.Parse(Path.GetFileName(fullPath));
                if (parsed.Quality != Qualities.Unknown) file.QualityId = parsed.Quality.Id;
                if (parsed.ReleaseGroup is not null) file.ReleaseGroup = parsed.ReleaseGroup;
                file.EmbeddedSubtitles = null;
                file.MediaInfo = null; // read again below, which also corrects the quality
                file.LastSubtitleSearch = null;
                file.SubtitlesNotFound = null;
                subtitlesChanged = true;
            }

            survivingFiles.Add(file);
        }

        // A surviving multi-episode file may cover an episode that was just added from metadata.
        foreach (var file in survivingFiles)
        {
            var fullPath = Path.Combine(series.Path, file.RelativePath);
            if (LibraryImport.SeriesLibraryImport.Identify(series.Path, fullPath) is not { } id) continue;
            foreach (var epNum in id.Episodes)
            {
                var ep = series.Episodes.FirstOrDefault(e => e.SeasonNumber == id.Season && e.EpisodeNumber == epNum);
                if (ep is not null && ep.EpisodeFileId is null)
                {
                    ep.EpisodeFileId = file.Id;
                    ep.EpisodeFile = file;
                }
            }
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var knownPaths = survivingFiles
            .Select(f => MediaFolder.Normalize(Path.Combine(series.Path, f.RelativePath)))
            .ToHashSet(comparison);
        var untrackedFiles = MediaFolder.VideoFiles(series.Path)
            .Where(f => !knownPaths.Contains(MediaFolder.Normalize(f.Path)))
            .ToList();

        if (untrackedFiles.Count > 0)
        {
            var (linked, _) = await LibraryImport.SeriesLibraryImport.LinkFilesAsync(db, series, untrackedFiles, ct);
            if (linked.Count > 0)
            {
                var imported = new HistoryEntry
                {
                    EventType = HistoryEventType.Imported,
                    SourceTitle = Path.GetFileName(Path.TrimEndingDirectorySeparator(series.Path)),
                    SeriesId = series.Id,
                    Message = $"Imported existing files: {Codes(linked)}",
                };
                db.History.Add(imported);
                historyEntries.Add(imported);
                subtitlesChanged = true;
            }
        }

        foreach (var file in survivingFiles)
        {
            if (await SubtitleService.SyncEpisodeFileAsync(db, series.Path, file, ct))
            {
                subtitlesChanged = true;
            }
        }

        if (seen is not null)
        {
            // Episodes removed upstream are dropped unless we hold a file for them on disk.
            foreach (var gone in series.Episodes.Where(e => !seen.Contains(e) && e.EpisodeFileId is null).ToList())
            {
                series.Episodes.Remove(gone);
                db.Episodes.Remove(gone);
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var entry in historyEntries)
        {
            await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        }

        if (subtitlesChanged)
        {
            await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)series.Id }, ct);
        }

        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);
    }

    /// <summary>Normalises a series' alternate title the way the decision engine compares series titles.</summary>
    public static string NormalizeTitle(string title) => SeriesNaming.NormalizedTitle(title);

    /// <summary>
    /// Replaces the series' metadata alternate titles with the ones from the metadata source in the languages worth
    /// keeping. <see cref="Core.Domain.Series.AlternateTitles"/> must be loaded. Does not save.
    /// </summary>
    private async Task ApplyAlternateTitlesAsync(Core.Domain.Series series, SeriesInfo info, CancellationToken ct)
    {
        var keep = await AlternateTitleLanguages.ForAsync(db, settings, series.Languages, info.OriginalLanguage, ct);
        var titles = AlternateTitles.Select(info.AlternateTitles, [series.Title, series.MetadataTitle], NormalizeTitle, keep);
        db.AlternateTitles.RemoveRange(AlternateTitles.Sync(series.AlternateTitles, AlternateTitleSource.Metadata, titles, NormalizeTitle));
    }

    /// <summary>
    /// Reads scene names and numbering from TheXEM when enabled (numbering only with TheTVDB metadata, whose order XEM
    /// maps). The result is kept on the series until the next refresh; when XEM cannot be reached the previous mapping
    /// stays. <see cref="Core.Domain.Series.AlternateTitles"/> must be loaded. Does not save.
    /// </summary>
    private async Task ApplySceneMappingAsync(Core.Domain.Series series, CancellationToken ct)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        if (!meta.SceneMapping || series.TvdbId is not { } tvdbId)
        {
            SceneNumbering.Apply(series.Episodes, []);
            db.AlternateTitles.RemoveRange(AlternateTitles.Sync(series.AlternateTitles, AlternateTitleSource.Scene, [], NormalizeTitle));
            return;
        }

        try
        {
            var names = (await xem.GetNamesAsync(tvdbId, ct)).Select(n => new AlternateTitleInfo(n));
            var titles = AlternateTitles.Select(names, [series.Title, series.MetadataTitle], NormalizeTitle);
            db.AlternateTitles.RemoveRange(AlternateTitles.Sync(series.AlternateTitles, AlternateTitleSource.Scene, titles, NormalizeTitle));
            var mappings = meta.SeriesSource == SeriesMetadataSource.Tvdb ? await xem.GetMappingsAsync(tvdbId, ct) : [];
            SceneNumbering.Apply(series.Episodes, mappings);
        }
        catch (MetadataException ex)
        {
            logger.LogWarning("Could not read TheXEM scene mapping for {Title}: {Message}", series.Title, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not read TheXEM scene mapping for {Title}: timed out", series.Title);
        }
    }

    /// <summary>True when another series (not <paramref name="exceptId"/>) has one of these ids.</summary>
    private Task<bool> InLibraryAsync(SeriesIds ids, int? exceptId, CancellationToken ct) =>
        db.Series.AnyAsync(s => s.Id != exceptId
            && ((ids.TmdbId != null && s.TmdbId == ids.TmdbId) || (ids.TvdbId != null && s.TvdbId == ids.TvdbId)), ct);

    private static void ApplyInfo(Core.Domain.Series s, SeriesInfo info)
    {
        s.TmdbId = info.TmdbId ?? s.TmdbId;
        s.TvdbId = info.TvdbId ?? s.TvdbId;
        // Keep a title the user edited; otherwise follow the metadata source.
        if (!HasCustomTitle(s)) s.Title = info.Title;
        s.MetadataTitle = info.Title;
        s.SortTitle = SortTitle(s.Title);
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

    /// <summary>Longest title a user can give a series.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>True when the user gave the series a title other than the metadata one.</summary>
    public static bool HasCustomTitle(Core.Domain.Series s) =>
        s.MetadataTitle.Length > 0 && !string.Equals(s.Title, s.MetadataTitle, StringComparison.Ordinal);

    /// <summary>
    /// Sets the user's title for the series. A blank title, or the metadata title itself, goes back to the metadata title.
    /// </summary>
    public static void SetTitle(Core.Domain.Series s, string title)
    {
        var trimmed = title.Trim();
        if (s.MetadataTitle.Length == 0) s.MetadataTitle = s.Title;
        s.Title = trimmed.Length == 0 ? s.MetadataTitle : trimmed;
        s.SortTitle = SortTitle(s.Title);
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
    public async Task<(IReadOnlyList<EpisodeDecision> Decisions, IReadOnlyList<IndexerError> Errors, IReadOnlyList<IndexerQueryStatus> Indexers)> SearchAsync(
        Core.Domain.Series series, int? seasonNumber, Episode? episode, bool requireMonitored, CancellationToken ct = default)
    {
        var season = episode?.SeasonNumber ?? seasonNumber;
        var result = await indexerSearch.SearchAsync(Criteria(series, season, episode), ct: ct);

        series.LastSearched = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var scope = new SearchScope(season, episode is null ? null : new HashSet<int> { episode.Id });
        var languages = (await settings.GetAsync<LanguageSettings>(ct)).For(series.Languages);
        var decisions = EpisodeDecisionEngine.Evaluate(series, series.QualityProfile!, series.Episodes, result.Releases, scope, requireMonitored, Today, languages);
        return (decisions, result.Errors, result.Indexers);
    }

    /// <summary>Interactive search for a season or an episode that hands out evaluated releases as each indexer answers (see <see cref="SearchStream"/>).</summary>
    public async IAsyncEnumerable<SearchStreamEvent<EpisodeReleaseDto>> SearchStreamAsync(
        Core.Domain.Series series, int? seasonNumber, Episode? episode, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var season = episode?.SeasonNumber ?? seasonNumber;
        var scope = new SearchScope(season, episode is null ? null : new HashSet<int> { episode.Id });
        var languages = (await settings.GetAsync<LanguageSettings>(ct)).For(series.Languages);

        var run = await indexerSearch.StartAsync(Criteria(series, season, episode), ct: ct);
        series.LastSearched = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var events = run.EventsAsync(
            releases => EpisodeDecisionEngine.Evaluate(series, series.QualityProfile!, series.Episodes, releases, scope, requireMonitored: false, Today, languages)
                .Select(EpisodeReleaseDto.From).ToList(),
            row => row.Release, ct);
        await foreach (var e in events) yield return e;
    }

    /// <summary>Search for a season or an episode, in the scene numbering releases use (see <see cref="SceneNumbering"/>).</summary>
    internal static SearchCriteria Criteria(Core.Domain.Series series, int? season, Episode? episode)
    {
        int? searchSeason = season, searchEpisode = null;
        if (episode is not null)
        {
            (searchSeason, searchEpisode) = SceneNumbering.SceneNumbers(episode);
        }
        else if (season is { } s)
        {
            searchSeason = SceneNumbering.SearchSeason(series.Episodes.Where(e => e.SeasonNumber == s), s);
        }

        return new SearchCriteria
        {
            Type = SearchType.Tv,
            Query = SeriesNaming.BaseTitle(series.Title),
            TvdbId = series.TvdbId,
            // Only fall back to the TMDB id: indexers that know both prefer it, but TV releases are mostly tagged by TVDB id.
            TmdbId = series.TvdbId is null ? series.TmdbId : null,
            Season = searchSeason,
            Episode = searchEpisode,
            AlternateTitles = series.AlternateTitles
                .Select(t => new AlternateTitleInfo(SeriesNaming.BaseTitle(t.Title), t.Language))
                .ToList(),
        };
    }

    /// <summary>
    /// Automatic search: for each season with wanted episodes, search the season (packs and single episodes),
    /// grab the best non-overlapping releases, then search individually for episodes that are still missing.
    /// </summary>
    public async Task<string> AutoSearchAsync(int seriesId, int? seasonNumber, IReadOnlyCollection<int>? episodeIds, CancellationToken ct = default)
    {
        using var activity = KrakarrTelemetry.ActivitySource.StartActivity("series search");
        activity?.SetTag(KrakarrTelemetry.MediaTypeTag, "series");
        activity?.SetTag("krakarr.series.id", seriesId);
        activity?.SetTag("krakarr.search.season", seasonNumber);
        activity?.SetTag("krakarr.search.episodes", episodeIds?.Count);
        try
        {
            var result = await AutoSearchCoreAsync(seriesId, seasonNumber, episodeIds, ct);
            activity?.SetTag("krakarr.search.result", result);
            return result;
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            throw;
        }
    }

    private async Task<string> AutoSearchCoreAsync(int seriesId, int? seasonNumber, IReadOnlyCollection<int>? episodeIds, CancellationToken ct)
    {
        var series = await LoadAsync(seriesId, ct) ?? throw new KeyNotFoundException($"Series {seriesId} not found");
        System.Diagnostics.Activity.Current?.SetTag("krakarr.series.title", series.Title);
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
                var (decisions, errs, _) = await SearchAsync(series, season.Key, null, requireMonitored: true, ct);
                errors += errs.Count;
                await GrabSelectionAsync(series, decisions, covered, grabbed, ct);
            }

            foreach (var episode in seasonWanted.Where(e => !covered.Contains(e.Id)).Take(20))
            {
                var (decisions, errs, _) = await SearchAsync(series, null, episode, requireMonitored: true, ct);
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
