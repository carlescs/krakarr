using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
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

public sealed record WantedSubtitle(SubtitleTarget Target, IReadOnlyList<string> MissingLanguages);

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
                Title = series.Title,
                Year = series.Year,
                SeasonNumber = episode.SeasonNumber,
                EpisodeNumber = episode.EpisodeNumber,
                ReleaseName = releaseName,
            },
        };
    }

    // ---- Existing subtitles ------------------------------------------------------------------

    /// <summary>Records subtitle files found next to the video and forgets rows whose file is gone.</summary>
    public async Task SyncDiskAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        var rows = await RowsAsync(target, ct);
        foreach (var gone in rows.Where(r => !File.Exists(r.Path)))
        {
            db.SubtitleFiles.Remove(gone);
        }

        var known = rows.Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        var baseName = Path.GetFileNameWithoutExtension(target.VideoPath);
        foreach (var path in SidecarFiles.SubtitlesFor(target.VideoPath).Where(p => !known.Contains(p)))
        {
            if (Languages.FromSidecarName(Path.GetFileName(path), baseName) is not { } info) continue;
            db.SubtitleFiles.Add(new SubtitleFile
            {
                MovieFileId = target.MovieFileId,
                EpisodeFileId = target.EpisodeFileId,
                Language = info.Language,
                HearingImpaired = info.HearingImpaired,
                Forced = info.Forced,
                Path = path,
                Provider = "disk",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private Task<List<SubtitleFile>> RowsAsync(SubtitleTarget target, CancellationToken ct) =>
        target.MovieFileId is { } mf
            ? db.SubtitleFiles.Where(s => s.MovieFileId == mf).ToListAsync(ct)
            : db.SubtitleFiles.Where(s => s.EpisodeFileId == target.EpisodeFileId).ToListAsync(ct);

    /// <summary>Wanted languages without a (non-forced) subtitle.</summary>
    public async Task<IReadOnlyList<string>> MissingLanguagesAsync(SubtitleTarget target, SubtitleSettings s, CancellationToken ct = default)
    {
        var have = (await RowsAsync(target, ct)).Where(r => !r.Forced).Select(r => r.Language).ToHashSet();
        return s.Languages.Where(l => !have.Contains(l)).ToList();
    }

    // ---- Search & download -------------------------------------------------------------------

    /// <summary>Queries every configured provider for the given languages; provider failures are reported, not thrown.</summary>
    public async Task<(IReadOnlyList<SubtitleCandidate> Candidates, IReadOnlyList<string> Errors)> SearchProvidersAsync(
        SubtitleTarget target, IReadOnlyList<string> languages, SubtitleSettings s, CancellationToken ct = default)
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
            TmdbId = target.MediaType == MediaType.Movie ? target.TmdbId : null,
            SeasonNumber = target.Video.SeasonNumber,
            EpisodeNumber = target.Video.EpisodeNumber,
            Languages = languages,
            FileHash = hash,
            FileSize = size,
        };

        var candidates = new List<SubtitleCandidate>();
        var errors = new List<string>();
        foreach (var provider in Providers.Where(p => p.IsConfigured(s)))
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
    /// Automatic search: downloads the best acceptable subtitle for each missing wanted language.
    /// Returns a short summary for the task log.
    /// </summary>
    public async Task<string> SearchMissingAsync(SubtitleTarget target, CancellationToken ct = default)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        await SyncDiskAsync(target, ct);
        var missing = await MissingLanguagesAsync(target, s, ct);
        await MarkSearchedAsync(target, ct);
        if (missing.Count == 0) return $"{target.Label}: all subtitles present";

        var (candidates, providerErrors) = await SearchProvidersAsync(target, missing, s, ct);
        var errors = providerErrors.ToList();
        var downloaded = new List<string>();
        foreach (var language in missing)
        {
            var ranked = SubtitleScorer.Rank(target.Video, candidates.Where(c => c.Language == language), s);
            foreach (var choice in ranked.Where(r => r.Acceptable).Take(3))
            {
                try
                {
                    await DownloadAsync(target, choice.Candidate, choice.Score, s, ct);
                    downloaded.Add(language);
                    break;
                }
                catch (SubtitleProviderException ex)
                {
                    errors.Add($"{choice.Candidate.Provider}: {ex.Message}");
                    if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }

        var stillMissing = missing.Except(downloaded).ToList();
        var summary = downloaded.Count > 0 ? $"downloaded {string.Join(", ", downloaded)}" : "nothing suitable found";
        if (stillMissing.Count > 0 && downloaded.Count > 0) summary += $"; still missing {string.Join(", ", stillMissing)}";
        if (errors.Count > 0) summary += $" ({string.Join("; ", errors.Distinct())})";
        return $"{target.Label}: {summary}";
    }

    /// <summary>Downloads a subtitle and stores it next to the video as "{video}.{lang}[.hi][.forced].{ext}".</summary>
    public async Task<SubtitleFile> DownloadAsync(SubtitleTarget target, SubtitleCandidate candidate, int? score, SubtitleSettings? s = null, CancellationToken ct = default)
    {
        s ??= await settings.GetAsync<SubtitleSettings>(ct);
        var provider = Providers.FirstOrDefault(p => p.Name == candidate.Provider)
            ?? throw new SubtitleProviderException($"Unknown subtitle provider {candidate.Provider}");
        if (!File.Exists(target.VideoPath))
            throw new SubtitleProviderException($"Video file not found: {target.VideoPath}");

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

    // ---- Wanted -------------------------------------------------------------------------------

    /// <summary>Library files missing at least one wanted language (based on known subtitle rows).</summary>
    public async Task<IReadOnlyList<WantedSubtitle>> WantedAsync(int limit, CancellationToken ct = default)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        if (s.Languages.Count == 0) return [];

        var subs = await db.SubtitleFiles.AsNoTracking().Where(x => !x.Forced)
            .Select(x => new { x.MovieFileId, x.EpisodeFileId, x.Language }).ToListAsync(ct);
        var movieLangs = subs.Where(x => x.MovieFileId != null).ToLookup(x => x.MovieFileId!.Value, x => x.Language);
        var episodeLangs = subs.Where(x => x.EpisodeFileId != null).ToLookup(x => x.EpisodeFileId!.Value, x => x.Language);

        var result = new List<WantedSubtitle>();
        foreach (var id in await db.MovieFiles.AsNoTracking().OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(ct))
        {
            var missing = s.Languages.Except(movieLangs[id]).ToList();
            if (missing.Count == 0) continue;
            if (await GetMovieTargetAsync(id, ct) is { } t) result.Add(new WantedSubtitle(t, missing));
            if (result.Count >= limit) return result;
        }

        foreach (var id in await db.EpisodeFiles.AsNoTracking().OrderBy(f => f.SeriesId).ThenBy(f => f.SeasonNumber).ThenBy(f => f.Id).Select(f => f.Id).ToListAsync(ct))
        {
            var missing = s.Languages.Except(episodeLangs[id]).ToList();
            if (missing.Count == 0) continue;
            if (await GetEpisodeTargetAsync(id, ct) is { } t) result.Add(new WantedSubtitle(t, missing));
            if (result.Count >= limit) break;
        }

        return result;
    }
}
