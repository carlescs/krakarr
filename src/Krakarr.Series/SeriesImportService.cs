using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Media.Files;
using Krakarr.Media.Parsing;
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
            try
            {
                await ImportAsync(download, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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

            var episodes = parsed.EpisodeNumbers
                .Select(n => series.Episodes.FirstOrDefault(e => e.SeasonNumber == season && e.EpisodeNumber == n))
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
            var better = targets.Where(e => e.EpisodeFile is { } f && Qualities.FindById(f.QualityId).Weight > quality.Weight).ToList();
            if (better.Count > 0)
            {
                problems.Add($"{fileName}: existing file is better for {SeriesService.Codes(better)}");
                continue;
            }

            await ImportFileAsync(series, targets, path, quality, parsed.ReleaseGroup ?? downloadParsed.ReleaseGroup, download.Name, media, ct);
            imported.AddRange(targets);
        }

        await FinishAsync(download, series, imported, problems, ct);
    }

    private async Task ImportFileAsync(
        Core.Domain.Series series, List<Episode> episodes, string sourcePath, Quality quality, string? releaseGroup, string sceneName,
        MediaManagementSettings media, CancellationToken ct)
    {
        var season = episodes[0].SeasonNumber;
        var ext = Path.GetExtension(sourcePath);
        var fileName = media.RenameFiles
            ? SeriesNaming.BuildEpisodeFileName(series, episodes, quality, releaseGroup, media.EpisodeFileFormat, ext)
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
    }

    private async Task FinishAsync(Download download, Core.Domain.Series? series, List<Episode> imported, List<string> problems, CancellationToken ct)
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
                Message = $"Imported {SeriesService.Codes(imported)}" + (problems.Count > 0 ? $" · skipped: {string.Join("; ", problems)}" : string.Empty),
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
