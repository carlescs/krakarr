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

namespace Krakarr.Movies;

/// <summary>Imports completed movie downloads into the library (hardlink + rename).</summary>
public sealed class MovieImportService(
    KrakarrDbContext db,
    ITorrentEngine engine,
    IFileTransfer fileTransfer,
    ISettingsService settings,
    IEventHub eventHub,
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
            try
            {
                await ImportAsync(download, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
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

        if (movie.MovieFile is { } existingFile && Qualities.FindById(existingFile.QualityId).Weight > quality.Weight)
        {
            await FailAsync(download, $"Import failed: existing file ({Qualities.FindById(existingFile.QualityId).Name}) is better", ct);
            return;
        }

        var ext = Path.GetExtension(video.Value.Path);
        var fileName = media.RenameFiles
            ? MovieNaming.BuildFileName(movie, media.MovieFileFormat, quality, parsed.ReleaseGroup, ext)
            : Path.GetFileName(video.Value.Path);
        var destination = Path.Combine(movie.Path, fileName);

        // Remove the old file if we are upgrading.
        if (movie.MovieFile is { } old)
        {
            var oldPath = Path.Combine(movie.Path, old.RelativePath);
            if (File.Exists(oldPath) && !string.Equals(oldPath, destination, StringComparison.Ordinal))
            {
                File.Delete(oldPath);
            }

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
            Message = $"{mode} → {destination}",
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        engine.MarkImported(download.InfoHash);

        logger.LogInformation("Imported {Source} to {Destination} ({Mode})", video.Value.Path, destination, mode);
        await eventHub.PublishAsync(EventNames.MovieUpdated, new { movie.Id }, ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
    }

    private async Task<(string Path, long Length)?> FindMainVideoFileAsync(Download download, int minSizeMb, CancellationToken ct)
    {
        IEnumerable<(string Path, long Length)> files = await engine.GetFilesAsync(download.InfoHash, ct);
        if (!files.Any())
        {
            var root = Path.Combine(download.SavePath, download.Name);
            if (File.Exists(root))
            {
                files = [(root, new FileInfo(root).Length)];
            }
            else if (Directory.Exists(root))
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length));
            }
        }

        var candidates = files
            .Where(f => ReleaseParser.IsVideoFile(f.Path) && File.Exists(f.Path))
            .Where(f => !Path.GetFileNameWithoutExtension(f.Path).Contains("sample", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .ToList();

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
    }
}
