using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles;

/// <summary>A movie or episode file we manage subtitles for.</summary>
public sealed record SubtitleTarget
{
    public MediaType MediaType { get; init; }
    public int? MovieFileId { get; init; }
    public int? EpisodeFileId { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public required string VideoPath { get; init; }
    public required string Label { get; init; }
    public required VideoInfo Video { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
}

/// <param name="NotFound">When the file was marked "not found" (every provider came up empty, or by hand); automatic searches skip it.</param>
/// <summary>Best subtitle per missing language, regardless of the minimum score (see <see cref="SubtitleService.BestMatchesAsync"/>).</summary>
public sealed record BestSubtitleMatches(IReadOnlyList<string> MissingLanguages, int MinimumScore, IReadOnlyList<ScoredSubtitle> Best, IReadOnlyList<string> Errors);

public sealed record WantedSubtitle(SubtitleTarget Target, IReadOnlyList<string> MissingLanguages, DateTimeOffset? NotFound = null);

/// <summary>Column the wanted list is sorted by; <see cref="Default"/> lists movies first, then episodes by series and season.</summary>
public enum WantedSort { Default, Type, Title, Missing }

/// <summary>One page of the wanted list, with the total for the filter and the totals per filter.</summary>
public sealed record WantedPage(IReadOnlyList<WantedSubtitle> Items, int Total, int Searching, int NotFound);

/// <summary>Finds, scores, downloads and tracks external subtitles for library files.</summary>
public sealed class SubtitleService(
    KrakarrDbContext db,
    IEnumerable<ISubtitleProvider> providers,
    ISettingsService settings,
    IEventHub eventHub,
    TimeProvider time,
    ILogger<SubtitleService> logger)
{
    public IReadOnlyList<ISubtitleProvider> Providers { get; } = providers.ToList();

    // ---- Targets -----------------------------------------------------------------------------

    public async Task<SubtitleTarget?> GetMovieTargetAsync(int movieFileId, CancellationToken ct = default)
    {
        var row = await db.MovieFiles.AsNoTracking()
            .Join(db.Movies.AsNoTracking(), f => f.MovieId, m => m.Id, (f, m) => new { File = f, Movie = m })
            .FirstOrDefaultAsync(x => x.File.Id == movieFileId, ct);
        if (row is null) return null;
        return new SubtitleTarget
        {
            MediaType = MediaType.Movie,
            MovieFileId = row.File.Id,
            MovieId = row.Movie.Id,
            VideoPath = Path.Combine(row.Movie.Path, row.File.RelativePath),
            Label = row.Movie.Year > 0 ? $"{row.Movie.Title} ({row.Movie.Year})" : row.Movie.Title,
            ImdbId = row.Movie.ImdbId,
            TmdbId = row.Movie.TmdbId,
            Video = new VideoInfo
            {
                MediaType = MediaType.Movie,
                Title = row.Movie.Title,
                Year = row.Movie.Year,
                ReleaseName = row.File.SceneName ?? Path.GetFileNameWithoutExtension(row.File.RelativePath),
            },
        };
    }

    public async Task<SubtitleTarget?> GetEpisodeTargetAsync(int episodeFileId, CancellationToken ct = default)
    {
        var file = await db.EpisodeFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == episodeFileId, ct);
        if (file is null) return null;
        var series = await db.Series.AsNoTracking().FirstAsync(s => s.Id == file.SeriesId, ct);
        var episode = await db.Episodes.AsNoTracking().Where(e => e.EpisodeFileId == file.Id).OrderBy(e => e.EpisodeNumber).FirstOrDefaultAsync(ct);
        if (episode is null) return null;

        // The original release name carries source, group and codec; the renamed library file usually does not.
        var releaseName = file.SceneName ?? Path.GetFileNameWithoutExtension(file.RelativePath);
        return new SubtitleTarget
        {
            MediaType = MediaType.Series,
            EpisodeFileId = file.Id,
            SeriesId = series.Id,
            VideoPath = Path.Combine(series.Path, file.RelativePath),
            Label = $"{series.Title} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
            ImdbId = series.ImdbId,
            TmdbId = series.TmdbId,
            Video = new VideoInfo
            {
                MediaType = MediaType.Series,
                // Providers know the series by its metadata title, not by a title the user edited.
                Title = series.MetadataTitle.Length > 0 ? series.MetadataTitle : series.Title,
                Year = series.Year,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                ReleaseName = releaseName,
            },
        };
    }

    // ---- Existing subtitles ------------------------------------------------------------------

    /// <summary>
    /// Records subtitle files found next to the video, forgets rows whose file is gone, and reads the subtitle tracks
    /// inside the video the first time the file is seen.
    /// </summary>
    public async Task SyncDiskAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        await SyncEmbeddedAsync(target, ct);
        await SyncSidecarsAsync(db, target.VideoPath, target.MovieFileId, target.EpisodeFileId, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Syncs external subtitle files next to a movie file, and reads embedded subtitle tracks and media info when not read yet.
    /// Does not save changes so the caller can batch saves.
    /// </summary>
    public static Task<bool> SyncMovieFileAsync(KrakarrDbContext db, string moviePath, MovieFile file, CancellationToken ct = default) =>
        SyncFileSubtitlesAsync(db, Path.Combine(moviePath, file.RelativePath), file.Id, null, file, ct);

    /// <summary>
    /// Syncs external subtitle files next to an episode file, and reads embedded subtitle tracks and media info when not read yet.
    /// Does not save changes so the caller can batch saves.
    /// </summary>
    public static Task<bool> SyncEpisodeFileAsync(KrakarrDbContext db, string seriesPath, EpisodeFile file, CancellationToken ct = default) =>
        SyncFileSubtitlesAsync(db, Path.Combine(seriesPath, file.RelativePath), null, file.Id, file, ct);

    private static async Task<bool> SyncFileSubtitlesAsync(
        KrakarrDbContext db, string videoPath, int? movieFileId, int? episodeFileId, ILibraryFile file, CancellationToken ct)
    {
        var changed = false;
        // One read of the headers gives both the media info and the subtitle tracks.
        if (MediaFileProbe.Probe(videoPath, file) is { } tracks)
        {
            file.EmbeddedSubtitles ??= EmbeddedSubtitleList.Format(EmbeddedSubtitleList.FromTracks(tracks));
            changed = true;
        }

        if (await SyncSidecarsAsync(db, videoPath, movieFileId, episodeFileId, ct))
        {
            changed = true;
        }

        return changed;
    }

    private static async Task<bool> SyncSidecarsAsync(KrakarrDbContext db, string videoPath, int? movieFileId, int? episodeFileId, CancellationToken ct)
    {
        var changed = false;
        var rows = movieFileId is { } mf
            ? await db.SubtitleFiles.Where(s => s.MovieFileId == mf).ToListAsync(ct)
            : await db.SubtitleFiles.Where(s => s.EpisodeFileId == episodeFileId).ToListAsync(ct);
        foreach (var gone in rows.Where(r => !File.Exists(r.Path)))
        {
            db.SubtitleFiles.Remove(gone);
            changed = true;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var known = rows.Select(r => r.Path).ToHashSet(comparison);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var path in SidecarFiles.SubtitlesFor(videoPath).Where(p => !known.Contains(p)))
        {
            if (Languages.FromSidecarName(Path.GetFileName(path), baseName) is not { } info) continue;
            db.SubtitleFiles.Add(new SubtitleFile
            {
                MovieFileId = movieFileId,
                EpisodeFileId = episodeFileId,
                Language = info.Language,
                HearingImpaired = info.HearingImpaired,
                Forced = info.Forced,
                Path = path,
                Provider = SubtitleUpgrade.DiskProvider,
            });
            changed = true;
        }

        return changed;
    }

    private async Task SyncEmbeddedAsync(SubtitleTarget target, CancellationToken ct)
    {
        if (await EmbeddedAsync(target, ct) is not null || !File.Exists(target.VideoPath)) return;
        var embedded = EmbeddedSubtitleList.Format(EmbeddedSubtitleList.FromTracks(EmbeddedSubtitles.Read(target.VideoPath)));
        if (target.MovieFileId is { } mf)
            await db.MovieFiles.Where(f => f.Id == mf).ExecuteUpdateAsync(x => x.SetProperty(f => f.EmbeddedSubtitles, embedded), ct);
        else
            await db.EpisodeFiles.Where(f => f.Id == target.EpisodeFileId).ExecuteUpdateAsync(x => x.SetProperty(f => f.EmbeddedSubtitles, embedded), ct);
        if (embedded.Length > 0) logger.LogDebug("Embedded subtitles in {Target}: {Languages}", target.Label, embedded);
    }

    /// <summary>The stored subtitle tracks inside the video ("" = none, null = not read yet).</summary>
    private Task<string?> EmbeddedAsync(SubtitleTarget target, CancellationToken ct) =>
        target.MovieFileId is { } mf
            ? db.MovieFiles.AsNoTracking().Where(f => f.Id == mf).Select(f => f.EmbeddedSubtitles).FirstOrDefaultAsync(ct)
            : db.EpisodeFiles.AsNoTracking().Where(f => f.Id == target.EpisodeFileId).Select(f => f.EmbeddedSubtitles).FirstOrDefaultAsync(ct);

    private Task<List<SubtitleFile>> RowsAsync(SubtitleTarget target, CancellationToken ct) =>
        target.MovieFileId is { } mf
            ? db.SubtitleFiles.Where(s => s.MovieFileId == mf).ToListAsync(ct)
            : db.SubtitleFiles.Where(s => s.EpisodeFileId == target.EpisodeFileId).ToListAsync(ct);

    /// <summary>Wanted languages without a (non-forced) subtitle, external or (when enabled) inside the video.</summary>
    public async Task<IReadOnlyList<string>> MissingLanguagesAsync(SubtitleTarget target, SubtitleSettings s, CancellationToken ct = default)
    {
        var have = (await RowsAsync(target, ct)).Where(r => !r.Forced).Select(r => r.Language).ToHashSet();
        if (s.UseEmbeddedSubtitles) have.UnionWith(EmbeddedSubtitleList.FullLanguages(await EmbeddedAsync(target, ct)));
        return s.Languages.Where(l => !have.Contains(l)).ToList();
    }

    // ---- Search & download -------------------------------------------------------------------

    /// <summary>Queries configured providers (optionally filtered to <paramref name="providers"/>) for the given languages; provider failures are reported, not thrown.</summary>
    public async Task<(IReadOnlyList<SubtitleCandidate> Candidates, IReadOnlyList<string> Errors)> SearchProvidersAsync(
        SubtitleTarget target, IReadOnlyList<string> languages, SubtitleSettings s, CancellationToken ct = default,
        IReadOnlyCollection<string>? providers = null)
    {
        string? hash = null;
        long size = 0;
        if (File.Exists(target.VideoPath))
        {
            size = new FileInfo(target.VideoPath).Length;
            hash = OpenSubtitlesHash.Compute(target.VideoPath);
        }

        var query = new SubtitleQuery
        {
            MediaType = target.MediaType,
            Title = target.Video.Title,
            Year = target.Video.Year,
            ImdbId = target.ImdbId,
            TmdbId = target.TmdbId,
            SeasonNumber = target.Video.SeasonNumber,
            EpisodeNumber = target.Video.EpisodeNumber,
            Languages = languages,
            FileHash = hash,
            FileSize = size,
        };

        var candidates = new List<SubtitleCandidate>();
        var errors = new List<string>();
        foreach (var provider in ConfiguredProviders(s, providers))
        {
            try
            {
                candidates.AddRange(await provider.SearchAsync(query, s, ct));
            }
            catch (SubtitleProviderException ex)
            {
                errors.Add($"{provider.Name}: {ex.Message}");
                logger.LogWarning("Subtitle search on {Provider} failed: {Message}", provider.Name, ex.Message);
            }
        }

        return (candidates.Where(c => languages.Contains(c.Language)).ToList(), errors);
    }

    /// <summary>
    /// Automatic search: downloads the best acceptable subtitle for each missing wanted language, and upgrades recent
    /// low-scoring downloads (see <see cref="SubtitleUpgrade.Select"/>): they are replaced when a candidate scores higher.
    /// With <paramref name="upgrade"/>, every language that already has a downloaded subtitle is searched too.
    /// Subtitles found on disk have no score and are never replaced, and languages present only inside the video
    /// (see <see cref="SubtitleSettings.UseEmbeddedSubtitles"/>) are not searched.
    /// Returns a short summary for the task log.
    /// </summary>
    public async Task<string> SearchMissingAsync(
        SubtitleTarget target, CancellationToken ct = default, bool upgrade = false, IReadOnlyCollection<string>? providers = null)
    {
        using var activity = KrakarrTelemetry.ActivitySource.StartActivity("subtitle search");
        activity?.SetTag("krakarr.subtitle.target", target.Label);
        activity?.SetTag("krakarr.subtitle.upgrade", upgrade);
        try
        {
            var result = await SearchMissingCoreAsync(target, upgrade, providers, ct);
            activity?.SetTag("krakarr.subtitle.result", result);
            return result;
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            throw;
        }
    }

    private async Task<string> SearchMissingCoreAsync(
        SubtitleTarget target, bool upgrade, IReadOnlyCollection<string>? providers, CancellationToken ct)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        await SyncDiskAsync(target, ct);
        var missing = await MissingLanguagesAsync(target, s, ct);
        // Score to beat per language already present; disk subtitles are left alone.
        var current = SubtitleUpgrade.Select(await RowsAsync(target, ct), s, time.GetUtcNow(), upgrade)
            .Where(kv => !missing.Contains(kv.Key)).ToDictionary();
        if (missing.Count > 0 || current.Count > 0)
        {
            // Don't query providers that asked us to back off, and leave the file due so it is searched once they allow it.
            if (BackoffUntil(s, providers) is { } until)
                return $"{target.Label}: skipped, subtitle providers paused until {until.ToLocalTime():g}";
        }

        await MarkSearchedAsync(target, ct);

        var languages = missing.Concat(current.Keys).ToList();
        if (languages.Count == 0)
        {
            await MarkNotFoundAsync(target, false, ct);
            return $"{target.Label}: all subtitles present";
        }

        var (candidates, providerErrors) = await SearchProvidersAsync(target, languages, s, ct, providers);
        var errors = providerErrors.ToList();
        var downloaded = new List<string>();
        var upgraded = new List<string>();
        foreach (var language in languages)
        {
            var toBeat = current.TryGetValue(language, out var score) ? score : -1;
            var ranked = SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == language), s);
            foreach (var choice in ranked.Where(r => r.Acceptable && r.Score > toBeat).Take(3))
            {
                try
                {
                    await DownloadAsync(target, choice.Candidate, choice.Score, s, ct);
                    (toBeat >= 0 ? upgraded : downloaded).Add(language);
                    break;
                }
                catch (SubtitleProviderException ex)
                {
                    errors.Add($"{choice.Candidate.Provider}: {ex.Message}");
                    if (ex is SubtitleRateLimitException || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }

        var stillMissing = missing.Except(downloaded).ToList();
        // Mark the file when every configured provider answered and none had a missing language, so the scheduled
        // search stops retrying it. Errors may be transient, and a search limited to some providers proves nothing.
        if (stillMissing.Count == 0)
            await MarkNotFoundAsync(target, false, ct);
        else if (errors.Count == 0 && providers is not { Count: > 0 } && ConfiguredProviders(s, null).Any())
            await MarkNotFoundAsync(target, true, ct);
        var parts = new List<string>();
        if (downloaded.Count > 0) parts.Add($"downloaded {string.Join(", ", downloaded)}");
        if (upgraded.Count > 0) parts.Add($"upgraded {string.Join(", ", upgraded)}");
        var summary = parts.Count > 0 ? string.Join("; ", parts) : current.Count > 0 && missing.Count == 0 ? "no better subtitles found" : "nothing suitable found";
        if (stillMissing.Count > 0 && parts.Count > 0) summary += $"; still missing {string.Join(", ", stillMissing)}";
        if (errors.Count > 0) summary += $" ({string.Join("; ", errors.Distinct())})";
        return $"{target.Label}: {summary}";
    }

    /// <summary>
    /// The best subtitle for each missing wanted language, even when it scores below the minimum automatic downloads
    /// need; the hearing-impaired, AI-translated and forced rules still apply, and a wrong episode (score 0) never
    /// qualifies. Nothing is downloaded: the caller confirms each choice and downloads it.
    /// </summary>
    public async Task<BestSubtitleMatches> BestMatchesAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        await SyncDiskAsync(target, ct);
        var missing = await MissingLanguagesAsync(target, s, ct);
        var minimum = SubtitleScorer.MinimumScore(target.Video, s);
        if (missing.Count == 0) return new BestSubtitleMatches(missing, minimum, [], []);
        if (BackoffUntil(s) is { } until)
            return new BestSubtitleMatches(missing, minimum, [], [$"Subtitle providers paused until {until.ToLocalTime():g}"]);

        var (candidates, errors) = await SearchProvidersAsync(target, missing, s, ct);
        var best = new List<ScoredSubtitle>();
        foreach (var language in missing)
        {
            var top = SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == language), s, ignoreMinimum: true)
                .FirstOrDefault(r => r.Acceptable && r.Score > 0);
            if (top is not null) best.Add(top);
        }

        return new BestSubtitleMatches(missing, minimum, best, errors);
    }

    /// <summary>
    /// When every selected configured provider asked us to back off (rate limit or quota), the earliest time one of
    /// them can be queried again; null when at least one can be queried now.
    /// </summary>
    public DateTimeOffset? BackoffUntil(SubtitleSettings s, IReadOnlyCollection<string>? providers = null)
    {
        DateTimeOffset? earliest = null;
        foreach (var provider in ConfiguredProviders(s, providers))
        {
            if (provider.BackoffUntil is not { } until) return null;
            if (earliest is null || until < earliest) earliest = until;
        }

        return earliest;
    }

    private IEnumerable<ISubtitleProvider> ConfiguredProviders(SubtitleSettings s, IReadOnlyCollection<string>? providers)
    {
        var configured = Providers.Where(p => p.IsConfigured(s));
        if (providers is not { Count: > 0 }) return configured;
        var allowed = new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);
        return configured.Where(p => allowed.Contains(p.Name));
    }

    /// <summary>Downloads a subtitle and stores it next to the video as "{video}.{lang}[.hi][.forced].{ext}".</summary>
    /// <param name="manual">Picked by hand in the interactive search: automatic upgrades leave it alone.</param>
    public async Task<SubtitleFile> DownloadAsync(SubtitleTarget target, SubtitleCandidate candidate, int? score, SubtitleSettings? s = null, CancellationToken ct = default,
        bool manual = false)
    {
        s ??= await settings.GetAsync<SubtitleSettings>(ct);
        var provider = Providers.FirstOrDefault(p => p.Name == candidate.Provider)
            ?? throw new SubtitleProviderException($"Unknown subtitle provider {candidate.Provider}");
        if (!File.Exists(target.VideoPath))
            throw new SubtitleProviderException($"Video file not found: {target.VideoPath}");

        using var activity = KrakarrTelemetry.ActivitySource.StartActivity("subtitle download");
        activity?.SetTag("krakarr.subtitle.provider", candidate.Provider);
        activity?.SetTag("krakarr.subtitle.language", candidate.Language);
        activity?.SetTag("krakarr.subtitle.score", score);
        var content = await provider.DownloadAsync(candidate, s, ct);
        if (content.Data.Length == 0) throw new SubtitleProviderException("Downloaded subtitle is empty");

        var path = SubtitlePath(target.VideoPath, candidate.Language, candidate.HearingImpaired, candidate.Forced, content.Extension);
        await File.WriteAllBytesAsync(path, content.Data, ct);

        // One subtitle per language/flags: replace a previous one.
        var rows = await RowsAsync(target, ct);
        foreach (var old in rows.Where(r => r.Language == candidate.Language && r.Forced == candidate.Forced))
        {
            if (old.Path != path && File.Exists(old.Path)) File.Delete(old.Path);
            db.SubtitleFiles.Remove(old);
        }

        var row = new SubtitleFile
        {
            MovieFileId = target.MovieFileId,
            EpisodeFileId = target.EpisodeFileId,
            Language = candidate.Language,
            HearingImpaired = candidate.HearingImpaired,
            Forced = candidate.Forced,
            Path = path,
            Provider = candidate.Provider,
            Score = score,
            Release = candidate.ReleaseNames.FirstOrDefault() ?? candidate.FileName,
            Manual = manual,
            DateAdded = time.GetUtcNow(),
        };
        db.SubtitleFiles.Add(row);
        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.SubtitleDownloaded,
            SourceTitle = row.Release ?? Path.GetFileName(path),
            MovieId = target.MovieId,
            SeriesId = target.SeriesId,
            Language = candidate.Language,
            Message = $"{Languages.Name(candidate.Language)} for {target.Label} from {candidate.Provider}" + (score is { } sc ? $" (score {sc})" : string.Empty),
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Downloaded {Language} subtitle for {Target} from {Provider} (score {Score})", candidate.Language, target.Label, candidate.Provider, score);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { target.MovieId, target.SeriesId }, ct);
        return row;
    }

    public static string SubtitlePath(string videoPath, string language, bool hearingImpaired, bool forced, string extension)
    {
        var dir = Path.GetDirectoryName(videoPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(videoPath) + "." + language
            + (hearingImpaired ? ".hi" : string.Empty)
            + (forced ? ".forced" : string.Empty)
            + (extension.StartsWith('.') ? extension : "." + extension);
        return Path.Combine(dir, name);
    }

    private async Task MarkSearchedAsync(SubtitleTarget target, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (target.MovieFileId is { } mf)
            await db.MovieFiles.Where(f => f.Id == mf).ExecuteUpdateAsync(x => x.SetProperty(f => f.LastSubtitleSearch, now), ct);
        else
            await db.EpisodeFiles.Where(f => f.Id == target.EpisodeFileId).ExecuteUpdateAsync(x => x.SetProperty(f => f.LastSubtitleSearch, now), ct);
    }

    /// <summary>
    /// Sets or clears the "not found" mark; a first mark is kept. Only movie files are marked automatically: episode
    /// subtitles often appear days after airing, so episode files are only marked by hand (<see cref="MarkAllNotFoundAsync"/>),
    /// but finding their subtitles clears the mark. Publishes <see cref="EventNames.SubtitlesUpdated"/> when the mark
    /// changes so the wanted list and its counts refresh.
    /// </summary>
    private async Task MarkNotFoundAsync(SubtitleTarget target, bool notFound, CancellationToken ct)
    {
        int changed;
        if (target.MovieFileId is not { } mf)
        {
            if (notFound || target.EpisodeFileId is not { } ef) return;
            changed = await db.EpisodeFiles.Where(f => f.Id == ef && f.SubtitlesNotFound != null).ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), ct);
        }
        else if (notFound)
        {
            var now = time.GetUtcNow();
            changed = await db.MovieFiles.Where(f => f.Id == mf && f.SubtitlesNotFound == null).ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, now), ct);
            if (changed > 0) logger.LogInformation("No subtitles found for {Target} on any provider; it will not be searched automatically again", target.Label);
        }
        else
        {
            changed = await db.MovieFiles.Where(f => f.Id == mf && f.SubtitlesNotFound != null).ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), ct);
        }

        if (changed > 0) await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { target.MovieId, target.SeriesId }, ct);
    }

    /// <summary>
    /// Clears the "not found" mark of one movie or episode file (or of every marked file when neither is given) and makes
    /// it due, so the scheduled search tries it again. Returns how many files were reset.
    /// </summary>
    public async Task<int> RetryNotFoundAsync(int? movieFileId, CancellationToken ct = default, int? episodeFileId = null)
    {
        var count = 0;
        if (episodeFileId is null)
        {
            var movies = db.MovieFiles.Where(f => f.SubtitlesNotFound != null);
            if (movieFileId is { } id) movies = movies.Where(f => f.Id == id);
            count += await movies.ExecuteUpdateAsync(x => x
                .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null)
                .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null), ct);
        }

        if (movieFileId is null)
        {
            var episodes = db.EpisodeFiles.Where(f => f.SubtitlesNotFound != null);
            if (episodeFileId is { } id) episodes = episodes.Where(f => f.Id == id);
            count += await episodes.ExecuteUpdateAsync(x => x
                .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null)
                .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null), ct);
        }

        if (count > 0)
        {
            logger.LogInformation("Subtitles: retrying {Count} file(s) where nothing was found", count);
            await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)null }, ct);
        }

        return count;
    }

    /// <summary>
    /// Marks one movie or episode file "not found" by hand (e.g. the best subtitle on offer was not good enough), so the
    /// scheduled search stops retrying it until it is retried. A file already marked keeps its mark. Returns 1 when marked.
    /// </summary>
    public async Task<int> MarkFileNotFoundAsync(int? movieFileId, int? episodeFileId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var count = movieFileId is { } mf
            ? await db.MovieFiles.Where(f => f.Id == mf && f.SubtitlesNotFound == null).ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, now), ct)
            : episodeFileId is { } ef
                ? await db.EpisodeFiles.Where(f => f.Id == ef && f.SubtitlesNotFound == null).ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, now), ct)
                : 0;
        if (count > 0) await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)null }, ct);
        return count;
    }

    /// <summary>
    /// Marks every movie and episode file still missing a wanted language "not found", so the scheduled search stops
    /// retrying them until they are retried (or replaced, or the wanted languages or providers change). Files already
    /// marked keep their mark. Returns how many files were marked.
    /// </summary>
    public async Task<int> MarkAllNotFoundAsync(CancellationToken ct = default)
    {
        // Ids only: the wanted scan already works from slim rows.
        var movieIds = new List<int>();
        var episodeIds = new List<int>();
        await foreach (var file in WantedFilesAsync(false, ct))
            (file.MediaType == MediaType.Movie ? movieIds : episodeIds).Add(file.Id);

        var now = time.GetUtcNow();
        var count = 0;
        foreach (var chunk in movieIds.Chunk(500))
            count += await db.MovieFiles.Where(f => chunk.Contains(f.Id) && f.SubtitlesNotFound == null)
                .ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, now), ct);
        foreach (var chunk in episodeIds.Chunk(500))
            count += await db.EpisodeFiles.Where(f => chunk.Contains(f.Id) && f.SubtitlesNotFound == null)
                .ExecuteUpdateAsync(x => x.SetProperty(f => f.SubtitlesNotFound, now), ct);

        if (count > 0)
        {
            logger.LogInformation("Subtitles: marked {Count} file(s) as not found; they will not be searched automatically until retried", count);
            await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = (int?)null, SeriesId = (int?)null }, ct);
        }

        return count;
    }

    // ---- Wanted -------------------------------------------------------------------------------

    /// <summary>
    /// Library files missing at least one wanted language (based on known subtitle rows and embedded tracks).
    /// <paramref name="notFound"/>: true = only files marked "not found", false = only unmarked files.
    /// </summary>
    public async Task<IReadOnlyList<WantedSubtitle>> WantedAsync(int limit, CancellationToken ct = default, bool? notFound = null)
    {
        var result = new List<WantedSubtitle>();
        await foreach (var file in WantedFilesAsync(notFound, ct))
        {
            var target = file.MediaType == MediaType.Movie ? await GetMovieTargetAsync(file.Id, ct) : await GetEpisodeTargetAsync(file.Id, ct);
            if (target is not null) result.Add(new WantedSubtitle(target, file.Missing, file.NotFound));
            if (result.Count >= limit) break;
        }

        return result;
    }

    /// <summary>
    /// One page of the files missing a wanted language, sorted on the server so the list can be scrolled page by page.
    /// Only the page's targets are built; the rest of the list stays as slim rows.
    /// </summary>
    public async Task<WantedPage> WantedPageAsync(int page, int pageSize, CancellationToken ct = default, bool? notFound = null,
        WantedSort sort = WantedSort.Default, bool desc = false)
    {
        var files = new List<WantedFile>();
        int searching = 0, marked = 0;
        await foreach (var file in WantedFilesAsync(null, ct))
        {
            if (file.NotFound is null) searching++;
            else marked++;
            if (notFound is null || (file.NotFound is not null) == notFound) files.Add(file);
        }

        // List.Sort is not stable: ties fall back to the default order (the index in the list).
        var keyed = files.Select((f, i) => (File: f, Index: i)).ToList();
        int Compare((WantedFile File, int Index) a, (WantedFile File, int Index) b)
        {
            var c = sort switch
            {
                WantedSort.Type => a.File.MediaType.CompareTo(b.File.MediaType),
                WantedSort.Missing => a.File.Missing.Count.CompareTo(b.File.Missing.Count),
                WantedSort.Title => CompareTitles(a.File, b.File),
                _ => a.Index.CompareTo(b.Index),
            };
            return (desc ? -c : c) is var d && d != 0 ? d : a.Index.CompareTo(b.Index);
        }
        keyed.Sort(Compare);

        var items = new List<WantedSubtitle>(pageSize);
        foreach (var (file, _) in keyed.Skip((page - 1) * pageSize).Take(pageSize))
        {
            var target = file.MediaType == MediaType.Movie ? await GetMovieTargetAsync(file.Id, ct) : await GetEpisodeTargetAsync(file.Id, ct);
            if (target is not null) items.Add(new WantedSubtitle(target, file.Missing, file.NotFound));
        }

        return new WantedPage(items, files.Count, searching, marked);
    }

    private static int CompareTitles(WantedFile a, WantedFile b)
    {
        var c = string.CompareOrdinal(a.SortTitle, b.SortTitle);
        if (c == 0) c = a.Number1.CompareTo(b.Number1);
        if (c == 0) c = a.Number2.CompareTo(b.Number2);
        return c;
    }

    /// <summary>How many files are missing a wanted language: still searched automatically, and marked "not found".</summary>
    public async Task<(int Searching, int NotFound)> WantedCountsAsync(CancellationToken ct = default)
    {
        int searching = 0, notFound = 0;
        await foreach (var file in WantedFilesAsync(null, ct))
        {
            if (file.NotFound is null) searching++;
            else notFound++;
        }

        return (searching, notFound);
    }

    /// <summary>A file missing a wanted language. Sorting by title uses the sort title, then year (movies) or season and episode.</summary>
    private sealed record WantedFile(MediaType MediaType, int Id, IReadOnlyList<string> Missing, DateTimeOffset? NotFound, string SortTitle, int Number1, int Number2);

    /// <summary>
    /// Files missing a wanted language, movies first, from slim rows (no targets are built). Episode files no episode points
    /// at are left out: they have no target to search for.
    /// </summary>
    private async IAsyncEnumerable<WantedFile> WantedFilesAsync(bool? notFound, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        if (s.Languages.Count == 0) yield break;

        var subs = await db.SubtitleFiles.AsNoTracking().Where(x => !x.Forced)
            .Select(x => new { x.MovieFileId, x.EpisodeFileId, x.Language }).ToListAsync(ct);
        var movieLangs = subs.Where(x => x.MovieFileId != null).ToLookup(x => x.MovieFileId!.Value, x => x.Language);
        var episodeLangs = subs.Where(x => x.EpisodeFileId != null).ToLookup(x => x.EpisodeFileId!.Value, x => x.Language);

        IEnumerable<string> Embedded(string? value) => s.UseEmbeddedSubtitles ? EmbeddedSubtitleList.FullLanguages(value) : [];

        var movieFiles = db.MovieFiles.AsNoTracking();
        if (notFound is { } nf) movieFiles = nf ? movieFiles.Where(f => f.SubtitlesNotFound != null) : movieFiles.Where(f => f.SubtitlesNotFound == null);
        var movieRows = await movieFiles
            .Join(db.Movies.AsNoTracking(), f => f.MovieId, m => m.Id, (f, m) => new { f.Id, f.EmbeddedSubtitles, f.SubtitlesNotFound, m.SortTitle, m.Year })
            .OrderBy(f => f.Id).ToListAsync(ct);
        foreach (var file in movieRows)
        {
            var missing = s.Languages.Except(movieLangs[file.Id]).Except(Embedded(file.EmbeddedSubtitles)).ToList();
            if (missing.Count > 0) yield return new WantedFile(MediaType.Movie, file.Id, missing, file.SubtitlesNotFound, file.SortTitle, file.Year, 0);
        }
        movieRows = null;

        // A few hundred series at most: one shared title per series instead of one string per episode row.
        var seriesTitles = await db.Series.AsNoTracking().Select(x => new { x.Id, x.SortTitle }).ToDictionaryAsync(x => x.Id, x => x.SortTitle, ct);

        var episodeFiles = db.EpisodeFiles.AsNoTracking();
        if (notFound is { } enf) episodeFiles = enf ? episodeFiles.Where(f => f.SubtitlesNotFound != null) : episodeFiles.Where(f => f.SubtitlesNotFound == null);
        foreach (var file in await episodeFiles.OrderBy(f => f.SeriesId).ThenBy(f => f.SeasonNumber).ThenBy(f => f.Id)
                     .Select(f => new
                     {
                         f.Id, f.SeriesId, f.SeasonNumber, f.EmbeddedSubtitles, f.SubtitlesNotFound,
                         Episode = db.Episodes.Where(e => e.EpisodeFileId == f.Id).Min(e => (int?)e.EpisodeNumber),
                     })
                     .ToListAsync(ct))
        {
            if (file.Episode is not { } episode) continue;
            var missing = s.Languages.Except(episodeLangs[file.Id]).Except(Embedded(file.EmbeddedSubtitles)).ToList();
            if (missing.Count > 0)
                yield return new WantedFile(MediaType.Series, file.Id, missing, file.SubtitlesNotFound, seriesTitles.GetValueOrDefault(file.SeriesId, ""), file.SeasonNumber, episode);
        }
    }
}
