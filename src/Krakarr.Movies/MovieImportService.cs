using System.Diagnostics;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Core.MediaServers;
using Krakarr.Core.Push;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Krakarr.Subtitles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Movies;

/// <summary>Imports completed movie downloads into the library (hardlink + rename).</summary>
public sealed class MovieImportService(
    KrakarrDbContext db,
    ITorrentEngine engine,
    IFileTransfer fileTransfer,
    ISettingsService settings,
    IEventHub eventHub,
    IMediaServerNotifier mediaServers,
    IPushNotifier push,
    ILogger<MovieImportService> logger) : IScheduledTask
{
    public string Name => "Import completed movie downloads";
    public TimeSpan Interval => TimeSpan.FromSeconds(15);
    public TimeSpan InitialDelay => TimeSpan.FromSeconds(20);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pending = await db.Downloads
            .Where(d => d.MovieId != null && d.CompletedAt != null && !d.Imported && d.ImportMessage == null)
            .ToListAsync(cancellationToken);

        foreach (var download in pending)
        {
            using var activity = KrakarrTelemetry.ActivitySource.StartActivity("import movie", ActivityKind.Internal, parentContext: default);
            activity?.SetTag(KrakarrTelemetry.MediaTypeTag, "movie");
            activity?.SetTag("krakarr.download.name", download.Name);
            activity?.SetTag("krakarr.download.info_hash", download.InfoHash);
            activity?.SetTag(KrakarrTelemetry.IndexerTag, download.IndexerName);
            try
            {
                await ImportAsync(download, cancellationToken);
                activity?.SetTag("krakarr.import.message", download.ImportMessage);
                if (!download.Imported) activity?.SetStatus(ActivityStatusCode.Error, download.ImportMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                activity.RecordException(ex);
                logger.LogError(ex, "Import of {Name} failed", download.Name);
                await FailAsync(download, $"Import failed: {ex.Message}", cancellationToken);
            }
        }
    }

    public async Task ImportAsync(Download download, CancellationToken ct)
    {
        var movie = await db.Movies.Include(m => m.MovieFile).FirstOrDefaultAsync(m => m.Id == download.MovieId, ct);
        if (movie is null)
        {
            await FailAsync(download, "Import failed: movie no longer exists", ct);
            return;
        }

        var media = await settings.GetAsync<MediaManagementSettings>(ct);
        var video = await FindMainVideoFileAsync(download, media.MinimumVideoFileSizeMb, ct);
        if (video is null)
        {
            await FailAsync(download, "Import failed: no video file found in download", ct);
            return;
        }

        var parsed = ReleaseParser.Parse(download.Name);
        var quality = download.QualityId is { } qid && qid > 0 ? Qualities.FindById(qid) : parsed.Quality;
        if (quality == Qualities.Unknown)
        {
            quality = ReleaseParser.ParseQuality(Path.GetFileName(video.Value.Path));
        }

        // The headers tell the real resolution and length: a fake "2160p" is judged as what it is, and samples are refused.
        var mediaInfo = MediaInfoReader.Read(video.Value.Path);
        var summary = MediaInfoSummary.From(mediaInfo);
        quality = MediaInfoChecks.Correct(quality, summary);
        if (MediaInfoChecks.TooShort(summary, movie.Runtime) is { } tooShort)
        {
            await FailAsync(download, $"Import failed: {tooShort}", ct);
            return;
        }

        if (movie.MovieFile is { } existingFile && Qualities.FindById(existingFile.QualityId).Weight > quality.Weight)
        {
            await FailAsync(download, $"Import failed: existing file ({Qualities.FindById(existingFile.QualityId).Name}) is better", ct);
            return;
        }

        var ext = Path.GetExtension(video.Value.Path);
        var fileName = media.RenameFiles
            ? MovieNaming.BuildFileName(movie, media.MovieFileFormat, quality, parsed.ReleaseGroup, ext, summary)
            : Path.GetFileName(video.Value.Path);
        var destination = Path.Combine(movie.Path, fileName);

        // Remove the old file if we are upgrading.
        var upgraded = movie.MovieFile is not null;
        if (movie.MovieFile is { } old)
        {
            var oldPath = Path.Combine(movie.Path, old.RelativePath);
            if (File.Exists(oldPath) && !string.Equals(oldPath, destination, StringComparison.Ordinal))
            {
                File.Delete(oldPath);
            }

            SidecarFiles.DeleteFor(oldPath); // subtitles of the old file; rows cascade with the MovieFile

            movie.MovieFileId = null;
            db.MovieFiles.Remove(old);
            await db.SaveChangesAsync(ct);
        }

        var mode = fileTransfer.Transfer(video.Value.Path, destination, media.UseHardlinks ? TransferMode.HardLink : TransferMode.Copy);

        var movieFile = new MovieFile
        {
            MovieId = movie.Id,
            RelativePath = fileName,
            Size = new FileInfo(destination).Length,
            QualityId = quality.Id,
            ReleaseGroup = parsed.ReleaseGroup,
            SceneName = download.Name,
            MediaInfo = MediaInfoSummary.Serialize(summary),
            EmbeddedSubtitles = EmbeddedSubtitleList.Format(EmbeddedSubtitleList.FromTracks(mediaInfo.Subtitles)),
        };
        db.MovieFiles.Add(movieFile);
        await db.SaveChangesAsync(ct);

        movie.MovieFileId = movieFile.Id;
        download.Imported = true;
        download.ImportedAt = DateTimeOffset.UtcNow;
        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.Imported,
            SourceTitle = download.Name,
            MovieId = movie.Id,
            QualityId = quality.Id,
            Indexer = download.IndexerName,
            Message = $"{mode} → {destination}" + Warnings(summary, download.Name, (await settings.GetAsync<LanguageSettings>(ct)).For(movie.Languages)),
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        engine.MarkImported(download.InfoHash);

        logger.LogInformation("Imported {Source} to {Destination} ({Mode})", video.Value.Path, destination, mode);
        mediaServers.PathChanged(movie.Path);
        await eventHub.PublishAsync(EventNames.MovieUpdated, new { movie.Id }, ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        push.Send(new PushMessage(PushKind.Imported, movie.Year > 0 ? $"{movie.Title} ({movie.Year})" : movie.Title,
            upgraded ? $"Upgraded to {quality.Name}" : $"Downloaded in {quality.Name}", $"/movies/{movie.Id}", $"movie-{movie.Id}",
            MovieId: movie.Id));
    }

    /// <summary>Media info warnings worth keeping in the history entry (mislabelled resolution, unwanted audio).</summary>
    private static string Warnings(MediaInfoSummary? summary, string releaseName, IReadOnlyList<string> wantedLanguages)
    {
        var warnings = new[] { MediaInfoChecks.Mislabelled(summary, releaseName), MediaInfoChecks.UnwantedAudio(summary, wantedLanguages) }
            .OfType<string>().ToList();
        return warnings.Count > 0 ? " · " + string.Join("; ", warnings) : string.Empty;
    }

    private async Task<(string Path, long Length)?> FindMainVideoFileAsync(Download download, int minSizeMb, CancellationToken ct)
    {
        var candidates = DownloadContents.VideoFiles(await engine.GetFilesAsync(download.InfoHash, ct), download.SavePath, download.Name);

        // Prefer files above the minimum size; fall back to the largest video if all are small.
        var big = candidates.FirstOrDefault(f => f.Length >= (long)minSizeMb * 1024 * 1024);
        return big != default ? big : candidates.Count > 0 ? candidates[0] : null;
    }

    private async Task FailAsync(Download download, string message, CancellationToken ct)
    {
        download.ImportMessage = message;
        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.DownloadFailed,
            SourceTitle = download.Name,
            MovieId = download.MovieId,
            QualityId = download.QualityId,
            Indexer = download.IndexerName,
            Message = message,
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("{Message} ({Name})", message, download.Name);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        push.Send(new PushMessage(PushKind.ImportFailed, download.Name, message, "/activity", $"failed-{download.Id}", MovieId: download.MovieId));
    }
}
