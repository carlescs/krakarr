using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles.Sync;

/// <summary>The sync tools found (full paths), each null when missing.</summary>
public sealed record SubtitleSyncTools(string? Alass, string? Ffsubsync, string? Ffmpeg)
{
    /// <summary>Something can sync against the video by itself (audio or an embedded subtitle track).</summary>
    public bool Any => Alass is not null || Ffsubsync is not null || Ffmpeg is not null;

    public static SubtitleSyncTools Find(SubtitleSyncSettings s) => new(
        Locate(s.AlassPath, "alass-cli", "alass"),
        Locate(s.FfsubsyncPath, "ffsubsync"),
        Locate(s.FfmpegPath, "ffmpeg"));

    /// <summary>The configured file when it exists, else the first of <paramref name="names"/> found on PATH.</summary>
    public static string? Locate(string? configured, params string[] names)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string[] suffixes = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var name in names)
            foreach (var dir in paths)
                foreach (var suffix in suffixes)
                {
                    var candidate = Path.Combine(dir, name + suffix);
                    if (File.Exists(candidate)) return candidate;
                }

        return null;
    }
}

/// <summary>Outcome of a sync, as shown to the user and stored in <see cref="SubtitleFile.SyncNote"/>.</summary>
public sealed record SubtitleSyncResult(bool Success, string Message);

/// <summary>Allows one sync at a time across the app: the tools decode the whole audio track and are CPU and memory hungry.</summary>
public sealed class SubtitleSyncGate
{
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Resynchronises external subtitles against the video. The reference is, in order: another subtitle of the same file
/// with a perfect score, a text subtitle track inside the video (extracted with ffmpeg), or the audio. alass is
/// preferred over ffsubsync; with a reference subtitle and neither installed, <see cref="SubtitleAligner"/> fixes a
/// constant shift or a framerate conversion. Also shifts subtitles by hand.
/// </summary>
public sealed class SubtitleSyncService(
    KrakarrDbContext db,
    ISettingsService settings,
    SubtitleSyncGate gate,
    SubtitleSyncProgress progress,
    IEventHub eventHub,
    TimeProvider time,
    ILogger<SubtitleSyncService> logger)
{
    /// <summary>A subtitle that scores this much matched the release exactly: good enough to time others against.</summary>
    private const int ReferenceScore = 100;

    public async Task<SubtitleSyncTools> ToolsAsync(CancellationToken ct = default) =>
        SubtitleSyncTools.Find((await settings.GetAsync<SubtitleSettings>(ct)).Sync);

    /// <summary>Shifts every cue of a subtitle by <paramref name="offsetMs"/> (negative = earlier).</summary>
    public async Task<SubtitleFile?> ShiftAsync(int subtitleId, int offsetMs, CancellationToken ct = default)
    {
        var row = await db.SubtitleFiles.FirstOrDefaultAsync(s => s.Id == subtitleId, ct);
        if (row is null) return null;
        if (!File.Exists(row.Path)) throw new FileNotFoundException($"{Path.GetFileName(row.Path)} no longer exists");

        await gate.Lock.WaitAsync(ct);
        try
        {
            SubtitleTiming.Transform(row.Path, offsetMs);
        }
        finally
        {
            gate.Lock.Release();
        }

        row.SyncNote = $"Shifted by {(offsetMs >= 0 ? "+" : "")}{offsetMs} ms by hand";
        row.SyncedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Shifted {Subtitle} by {Offset} ms", Path.GetFileName(row.Path), offsetMs);
        await PublishAsync(row, ct);
        return row;
    }

    /// <summary>Syncs one subtitle and records the outcome on its row. Returns null when the subtitle doesn't exist.</summary>
    public async Task<SubtitleSyncResult?> SyncAsync(int subtitleId, CancellationToken ct = default)
    {
        var row = await db.SubtitleFiles.FirstOrDefaultAsync(s => s.Id == subtitleId, ct);
        if (row is null) return null;

        using var activity = KrakarrTelemetry.ActivitySource.StartActivity("subtitle sync");
        activity?.SetTag("krakarr.subtitle.language", row.Language);
        var (title, link) = await DescribeAsync(row, ct);
        var cancel = progress.Queue(row.Id, title, link);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancel);
        var token = linked.Token;
        SubtitleSyncResult result;
        try
        {
            await gate.Lock.WaitAsync(token);
            try
            {
                progress.Stage(row.Id, "Looking for a reference");
                result = await SyncCoreAsync(row, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException
                                           or NotSupportedException or InvalidDataException)
            {
                result = new SubtitleSyncResult(false, ex.Message);
            }
            finally
            {
                gate.Lock.Release();
            }

            activity?.SetTag("krakarr.subtitle.result", result.Message);
            var note = result.Success ? result.Message : "Sync failed: " + result.Message;
            row.SyncNote = note.Length > 512 ? note[..512] : note;
            if (result.Success) row.SyncedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            progress.Complete(row.Id, result.Success, result.Message);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cancelled from the tasks menu. The note keeps the automatic task from starting it again; a sync by hand still can.
            result = new SubtitleSyncResult(false, SubtitleSyncProgress.CancelledResult);
            activity?.SetTag("krakarr.subtitle.result", "cancelled");
            row.SyncNote = "Sync cancelled";
            await db.SaveChangesAsync(ct);
            progress.Complete(row.Id, false, result.Message, cancelled: true);
        }
        finally
        {
            progress.Finish(row.Id); // cancelled or failed to save: no outcome to show
        }

        if (result.Success) logger.LogInformation("Synced {Subtitle}: {Result}", Path.GetFileName(row.Path), result.Message);
        else logger.LogWarning("Could not sync {Subtitle}: {Result}", Path.GetFileName(row.Path), result.Message);
        await PublishAsync(row, ct);
        return result;
    }

    private async Task<SubtitleSyncResult> SyncCoreAsync(SubtitleFile row, CancellationToken ct)
    {
        if (!File.Exists(row.Path)) return new(false, $"{Path.GetFileName(row.Path)} no longer exists");
        var videoPath = await VideoPathAsync(row, ct);
        if (videoPath is null || !File.Exists(videoPath)) return new(false, "The video file was not found");

        var s = (await settings.GetAsync<SubtitleSettings>(ct)).Sync;
        var tools = SubtitleSyncTools.Find(s);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(s.TimeoutSeconds, 30, 3600));
        var workDir = Path.Combine(Path.GetTempPath(), "krakarr-sync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var (reference, referenceName) = await ReferenceSubtitleAsync(row, videoPath, tools, workDir, timeout, ct);
            if (reference is null && tools.Alass is null && tools.Ffsubsync is null)
                return new(false, tools.Ffmpeg is null
                    ? "Install alass or ffsubsync (or ffmpeg, for videos with a text subtitle track) to sync subtitles"
                    : "No reference to sync against: the video has no text subtitle track; install alass or ffsubsync to sync against the audio");

            // Synced against a reference subtitle without a tool: constant shift and framerate only.
            if (reference is not null && tools.Alass is null && tools.Ffsubsync is null)
            {
                if (!SubtitleTiming.IsSupported(row.Path))
                    return new(false, $"Can't sync {Path.GetExtension(row.Path)} subtitles without alass or ffsubsync");
                var alignment = SubtitleAligner.Align(SubtitleTiming.ReadCues(reference), SubtitleTiming.ReadCues(row.Path));
                if (alignment is null) return new(false, $"Could not line it up with {referenceName}");
                if (Math.Abs(alignment.OffsetMs) < 1 && alignment.Ratio == 1) return new(true, $"Already in sync with {referenceName}");
                SubtitleTiming.Transform(row.Path, alignment.OffsetMs, alignment.Ratio);
                return new(true, $"Shifted {alignment.Describe()} to match {referenceName}");
            }

            var output = Path.Combine(workDir, "synced" + Path.GetExtension(row.Path));
            var against = reference ?? videoPath;
            var againstName = referenceName ?? "the audio";
            var toolName = tools.Alass is not null ? "alass" : "ffsubsync";
            progress.Stage(row.Id, $"Syncing with {toolName} against {againstName}");
            void OnLine(string line) => progress.ReportLine(row.Id, line);
            ProcessResult run;
            if (tools.Alass is { } alass)
            {
                Dictionary<string, string>? env = null;
                if (tools.Ffmpeg is { } ffmpeg)
                {
                    env = new() { ["ALASS_FFMPEG_PATH"] = ffmpeg };
                    if (SubtitleSyncTools.Locate(Path.Combine(Path.GetDirectoryName(ffmpeg) ?? ".", "ffprobe")) is { } probe) env["ALASS_FFPROBE_PATH"] = probe;
                }

                run = await ExternalProcess.RunAsync(alass, [against, row.Path, output], timeout, ct, env, OnLine);
            }
            else
            {
                List<string> args = [against, "-i", row.Path, "-o", output];
                if (tools.Ffmpeg is { } ffmpeg && Path.GetDirectoryName(ffmpeg) is { Length: > 0 } dir) args.AddRange(["--ffmpeg-path", dir]);
                run = await ExternalProcess.RunAsync(tools.Ffsubsync!, args, timeout, ct, onLine: OnLine);
            }

            if (!run.Success || !File.Exists(output) || new FileInfo(output).Length == 0)
                return new(false, $"{toolName}: {run.LastLine}");

            File.Copy(output, row.Path, overwrite: true);
            return new(true, $"Synced with {toolName} against {againstName}");
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A subtitle with the video's timing: a perfect-score subtitle of the same file, else a text track inside the video
    /// extracted with ffmpeg. Null when there is none (sync against the audio then).
    /// </summary>
    private async Task<(string? Path, string? Name)> ReferenceSubtitleAsync(
        SubtitleFile row, string videoPath, SubtitleSyncTools tools, string workDir, TimeSpan timeout, CancellationToken ct)
    {
        var siblings = await db.SubtitleFiles.AsNoTracking()
            .Where(x => x.Id != row.Id && !x.Forced && x.Score >= ReferenceScore
                        && (row.MovieFileId != null ? x.MovieFileId == row.MovieFileId : x.EpisodeFileId == row.EpisodeFileId))
            .Select(x => new { x.Path, x.Language }).ToListAsync(ct);
        foreach (var sibling in siblings)
        {
            if (File.Exists(sibling.Path) && SubtitleTiming.IsSupported(sibling.Path))
                return (sibling.Path, $"the {Languages.Name(sibling.Language)} subtitle");
        }

        if (tools.Ffmpeg is not { } ffmpeg) return (null, null);
        var tracks = EmbeddedSubtitles.Read(videoPath);
        // Subtitle tracks are numbered in file order, as ffmpeg's "0:s:N" counts them. Forced tracks have too few cues.
        var index = -1;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (!IsTextTrack(tracks[i].Codec)) continue;
            if (index < 0 || (tracks[index].Forced && !tracks[i].Forced)) index = i;
            if (!tracks[index].Forced) break;
        }

        if (index < 0) return (null, null);
        progress.Stage(row.Id, "Extracting the subtitle track inside the video");
        var output = Path.Combine(workDir, "reference.srt");
        var run = await ExternalProcess.RunAsync(ffmpeg,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath, "-map", $"0:s:{index}", "-c:s", "srt", output], timeout, ct);
        if (!run.Success || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            logger.LogDebug("Could not extract subtitle track {Index} of {Video}: {Error}", index, videoPath, run.LastLine);
            return (null, null);
        }

        var language = tracks[index].Language is { } tag ? EmbeddedSubtitleList.NormalizeLanguage(tag) : null;
        return (output, language is null ? "the subtitle track inside the video" : $"the {Languages.Name(language)} track inside the video");
    }

    /// <summary>Text subtitle codecs ffmpeg can convert to SubRip (not picture-based ones such as PGS or VobSub).</summary>
    public static bool IsTextTrack(string? codec) => codec is not null
        && (codec.StartsWith("S_TEXT/", StringComparison.OrdinalIgnoreCase)
            || codec is "tx3g" or "wvtt" or "text" or "S_SSA" or "S_ASS");

    /// <summary>
    /// What the bell calls a sync of <paramref name="row"/> ("Dune (2021) · English", "Severance S01E03 · Spanish") and
    /// the page it links to.
    /// </summary>
    public async Task<(string Title, string Link)> DescribeAsync(SubtitleFile row, CancellationToken ct = default)
    {
        var language = Languages.Name(row.Language) + (row.Forced ? " (forced)" : "");
        if (row.MovieFileId is { } mf)
        {
            var movie = await db.MovieFiles.AsNoTracking().Where(f => f.Id == mf)
                .Join(db.Movies.AsNoTracking(), f => f.MovieId, m => m.Id, (f, m) => new { m.Id, m.Title, m.Year }).FirstOrDefaultAsync(ct);
            return movie is null
                ? ($"{Path.GetFileName(row.Path)}", "/subtitles")
                : ($"{movie.Title}{(movie.Year > 0 ? $" ({movie.Year})" : "")} · {language}", $"/movies/{movie.Id}");
        }

        var file = await db.EpisodeFiles.AsNoTracking().Where(f => f.Id == row.EpisodeFileId)
            .Join(db.Series.AsNoTracking(), f => f.SeriesId, s => s.Id, (f, s) => new { f.Id, SeriesId = s.Id, s.Title }).FirstOrDefaultAsync(ct);
        if (file is null) return ($"{Path.GetFileName(row.Path)}", "/subtitles");
        var episode = await db.Episodes.AsNoTracking().Where(e => e.EpisodeFileId == file.Id)
            .OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber)
            .Select(e => new { e.SeasonNumber, e.EpisodeNumber }).FirstOrDefaultAsync(ct);
        var number = episode is null ? "" : $" S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
        return ($"{file.Title}{number} · {language}", $"/series/{file.SeriesId}");
    }

    private async Task<string?> VideoPathAsync(SubtitleFile row, CancellationToken ct)
    {
        if (row.MovieFileId is { } mf)
        {
            var x = await db.MovieFiles.AsNoTracking().Where(f => f.Id == mf)
                .Join(db.Movies.AsNoTracking(), f => f.MovieId, m => m.Id, (f, m) => new { m.Path, f.RelativePath }).FirstOrDefaultAsync(ct);
            return x is null ? null : Path.Combine(x.Path, x.RelativePath);
        }

        var e = await db.EpisodeFiles.AsNoTracking().Where(f => f.Id == row.EpisodeFileId)
            .Join(db.Series.AsNoTracking(), f => f.SeriesId, s => s.Id, (f, s) => new { s.Path, f.RelativePath }).FirstOrDefaultAsync(ct);
        return e is null ? null : Path.Combine(e.Path, e.RelativePath);
    }

    private async Task PublishAsync(SubtitleFile row, CancellationToken ct)
    {
        int? movieId = null, seriesId = null;
        if (row.MovieFileId is { } mf) movieId = await db.MovieFiles.AsNoTracking().Where(f => f.Id == mf).Select(f => (int?)f.MovieId).FirstOrDefaultAsync(ct);
        else seriesId = await db.EpisodeFiles.AsNoTracking().Where(f => f.Id == row.EpisodeFileId).Select(f => (int?)f.SeriesId).FirstOrDefaultAsync(ct);
        await eventHub.PublishAsync(EventNames.SubtitlesUpdated, new { MovieId = movieId, SeriesId = seriesId }, ct);
    }
}
