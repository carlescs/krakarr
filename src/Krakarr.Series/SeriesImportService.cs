using System.Diagnostics;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Core.MediaServers;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
using Krakarr.Subtitles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

/// <summary>Imports completed series downloads: maps each video file to episodes, renames and hardlinks it.</summary>
public sealed class SeriesImportService(
    KrakarrDbContext db,
    SeriesService seriesService,
    ITorrentEngine engine,
    IFileTransfer fileTransfer,
    ISettingsService settings,
    IEventHub eventHub,
    IMediaServerNotifier mediaServers,
    ILogger<SeriesImportService> logger) : IScheduledTask
{
    public string Name => "Import completed series downloads";
    public TimeSpan Interval => TimeSpan.FromSeconds(15);
    public TimeSpan InitialDelay => TimeSpan.FromSeconds(25);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var pending = await db.Downloads
            .Where(d => d.SeriesId != null && d.CompletedAt != null && !d.Imported && d.ImportMessage == null)
            .ToListAsync(cancellationToken);

        foreach (var download in pending)
        {
            using var activity = KrakarrTelemetry.ActivitySource.StartActivity("import series", ActivityKind.Internal, parentContext: default);
            activity?.SetTag(KrakarrTelemetry.MediaTypeTag, "series");
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
                await FinishAsync(download, null, [], [$"Import failed: {ex.Message}"], cancellationToken);
            }
        }
    }

    public async Task ImportAsync(Download download, CancellationToken ct)
    {
        var series = await seriesService.LoadAsync(download.SeriesId!.Value, ct);
        if (series is null)
        {
            await FinishAsync(download, null, [], ["Import failed: series no longer exists"], ct);
            return;
        }

        var media = await settings.GetAsync<MediaManagementSettings>(ct);
        var files = DownloadContents.VideoFiles(await engine.GetFilesAsync(download.InfoHash, ct), download.SavePath, download.Name);
        var minBytes = (long)media.MinimumVideoFileSizeMb * 1024 * 1024;
        if (files.Any(f => f.Length >= minBytes))
        {
            files = files.Where(f => f.Length >= minBytes).ToList(); // drop extras/featurettes when real episodes exist
        }

        if (files.Count == 0)
        {
            await FinishAsync(download, series, [], ["Import failed: no video files found in download"], ct);
            return;
        }

        var downloadParsed = ReleaseParser.Parse(download.Name);
        var imported = new List<Episode>();
        var problems = new List<string>();
        var notes = new List<string>();
        var wanted = (await settings.GetAsync<LanguageSettings>(ct)).For(series.Languages);

        foreach (var (path, _) in files)
        {
            var fileName = Path.GetFileName(path);
            var parsed = ReleaseParser.Parse(fileName);
            if (!parsed.IsEpisode && files.Count == 1)
            {
                parsed = downloadParsed; // single file with an uninformative name: trust the release name
            }

            if (!parsed.IsEpisode || parsed.SeasonNumber is not { } season)
            {
                problems.Add($"{fileName}: could not determine the episode");
                continue;
            }

            // Scene numbering first: the file is named like the release (see SceneNumbering).
            var episodes = parsed.EpisodeNumbers
                .Select(n => SceneNumbering.Find(series.Episodes, season, n))
                .Distinct()
                .ToList();
            if (episodes.Count == 0 || episodes.Any(e => e is null))
            {
                problems.Add($"{fileName}: {SeriesNaming.EpisodeCode(season, parsed.EpisodeNumbers)} is not a known episode");
                continue;
            }

            var quality = parsed.Quality != Qualities.Unknown ? parsed.Quality
                : download.QualityId is { } qid && qid > 0 ? Qualities.FindById(qid)
                : downloadParsed.Quality;

            var targets = episodes.Cast<Episode>().ToList();

            // The headers tell the real resolution and length: a fake "2160p" is judged as what it is, and samples are refused.
            var mediaInfo = MediaInfoReader.Read(path);
            var summary = MediaInfoSummary.From(mediaInfo);
            quality = MediaInfoChecks.Correct(quality, summary);
            if (MediaInfoChecks.TooShort(summary, Runtime(series, targets)) is { } tooShort)
            {
                problems.Add($"{fileName}: {tooShort}");
                continue;
            }

            var better = targets.Where(e => e.EpisodeFile is { } f && Qualities.FindById(f.QualityId).Weight > quality.Weight).ToList();
            if (better.Count > 0)
            {
                problems.Add($"{fileName}: existing file is better for {SeriesService.Codes(better)}");
                continue;
            }

            // Keep the file's own release name when it is informative (season packs), for subtitle matching later.
            var sceneName = ReleaseParser.Parse(fileName).IsEpisode ? Path.GetFileNameWithoutExtension(fileName) : download.Name;
            await ImportFileAsync(series, targets, path, quality, parsed.ReleaseGroup ?? downloadParsed.ReleaseGroup, sceneName, mediaInfo, summary, media, ct);
            if (MediaInfoChecks.Mislabelled(summary, sceneName) is { } mislabelled) notes.Add($"{fileName}: {mislabelled}");
            if (MediaInfoChecks.UnwantedAudio(summary, wanted) is { } audio) notes.Add($"{fileName}: {audio}");
            imported.AddRange(targets);
        }

        await FinishAsync(download, series, imported, problems, notes, ct);
    }

    /// <summary>Runtime the file should roughly have: its episodes' runtimes, or the series runtime where an episode has none.</summary>
    private static int Runtime(Core.Domain.Series series, IReadOnlyList<Episode> episodes) =>
        episodes.Sum(e => e.Runtime > 0 ? e.Runtime : series.Runtime);

    private async Task ImportFileAsync(
        Core.Domain.Series series, List<Episode> episodes, string sourcePath, Quality quality, string? releaseGroup, string sceneName,
        MediaInfo mediaInfo, MediaInfoSummary? summary, MediaManagementSettings media, CancellationToken ct)
    {
        var season = episodes[0].SeasonNumber;
        var ext = Path.GetExtension(sourcePath);
        var fileName = media.RenameFiles
            ? SeriesNaming.BuildEpisodeFileName(series, episodes, quality, releaseGroup, media.EpisodeFileFormat, ext, summary)
            : Path.GetFileName(sourcePath);
        var relative = series.SeasonFolder ? Path.Combine(SeriesNaming.BuildSeasonFolder(season, media.SeasonFolderFormat), fileName) : fileName;
        var destination = Path.Combine(series.Path, relative);

        // Replace existing files of these episodes (upgrade).
        foreach (var old in episodes.Select(e => e.EpisodeFile).OfType<EpisodeFile>().DistinctBy(f => f.Id).ToList())
        {
            var oldPath = Path.Combine(series.Path, old.RelativePath);
            if (File.Exists(oldPath) && !string.Equals(oldPath, destination, StringComparison.Ordinal))
            {
                File.Delete(oldPath);
            }

            SidecarFiles.DeleteFor(oldPath); // subtitles of the old file; rows cascade with the EpisodeFile

            foreach (var e in series.Episodes.Where(e => e.EpisodeFileId == old.Id))
            {
                e.EpisodeFileId = null;
                e.EpisodeFile = null;
            }

            db.EpisodeFiles.Remove(old);
        }

        var mode = fileTransfer.Transfer(sourcePath, destination, media.UseHardlinks ? TransferMode.HardLink : TransferMode.Copy);
        var episodeFile = new EpisodeFile
        {
            SeriesId = series.Id,
            SeasonNumber = season,
            RelativePath = relative,
            Size = new FileInfo(destination).Length,
            QualityId = quality.Id,
            ReleaseGroup = releaseGroup,
            SceneName = sceneName,
            MediaInfo = MediaInfoSummary.Serialize(summary),
            EmbeddedSubtitles = EmbeddedSubtitleList.Format(EmbeddedSubtitleList.FromTracks(mediaInfo.Subtitles)),
        };
        db.EpisodeFiles.Add(episodeFile);
        await db.SaveChangesAsync(ct);

        foreach (var e in episodes)
        {
            e.EpisodeFileId = episodeFile.Id;
            e.EpisodeFile = episodeFile;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Source} to {Destination} ({Mode})", sourcePath, destination, mode);
        mediaServers.PathChanged(Path.GetDirectoryName(destination) ?? series.Path);
    }

    private Task FinishAsync(Download download, Core.Domain.Series? series, List<Episode> imported, List<string> problems, CancellationToken ct) =>
        FinishAsync(download, series, imported, problems, [], ct);

    /// <param name="notes">Media info warnings about imported files, added to the history entry.</param>
    private async Task FinishAsync(Download download, Core.Domain.Series? series, List<Episode> imported, List<string> problems, List<string> notes, CancellationToken ct)
    {
        HistoryEntry entry;
        if (imported.Count > 0)
        {
            download.Imported = true;
            download.ImportedAt = DateTimeOffset.UtcNow;
            entry = new HistoryEntry
            {
                EventType = HistoryEventType.Imported,
                SourceTitle = download.Name,
                SeriesId = download.SeriesId,
                QualityId = download.QualityId,
                Indexer = download.IndexerName,
                Message = $"Imported {SeriesService.Codes(imported)}" + (problems.Count > 0 ? $" · skipped: {string.Join("; ", problems)}" : string.Empty)
                    + (notes.Count > 0 ? $" · {string.Join("; ", notes)}" : string.Empty),
            };
        }
        else
        {
            download.ImportMessage = problems.Count == 1 && problems[0].StartsWith("Import failed", StringComparison.Ordinal)
                ? problems[0]
                : $"Import failed: {string.Join("; ", problems)}";
            entry = new HistoryEntry
            {
                EventType = HistoryEventType.DownloadFailed,
                SourceTitle = download.Name,
                SeriesId = download.SeriesId,
                QualityId = download.QualityId,
                Indexer = download.IndexerName,
                Message = download.ImportMessage,
            };
            logger.LogWarning("{Message} ({Name})", download.ImportMessage, download.Name);
        }

        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        if (download.Imported) engine.MarkImported(download.InfoHash);
        if (series is not null) await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
    }
}
