using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.MediaServers;
using Krakarr.Data;
using Krakarr.Media.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

/// <summary>One episode file whose path differs from what the naming settings produce.</summary>
public sealed record OrganizeItem(int EpisodeFileId, string Existing, string New, string? Problem = null);

/// <summary>What organizing a series would change. <see cref="NewPath"/> is null when the series folder already matches (or cannot be renamed).</summary>
public sealed record OrganizePreview(string Path, string? NewPath, IReadOnlyList<OrganizeItem> Files);

public sealed record OrganizeRequest(bool RenameFolder);

/// <summary>
/// Renames and moves the files of a series so they follow the configured naming formats (episode file, season folder and,
/// optionally, the series folder). Subtitle files next to a video are renamed with it.
/// </summary>
public sealed class SeriesOrganizer(KrakarrDbContext db, ISettingsService settings, IEventHub eventHub, IMediaServerNotifier mediaServers, ILogger<SeriesOrganizer> logger)
{
    public async Task<OrganizePreview?> PreviewAsync(int seriesId, CancellationToken ct = default)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null) return null;
        var media = await settings.GetAsync<MediaManagementSettings>(ct);
        var files = await db.EpisodeFiles.AsNoTracking().Where(f => f.SeriesId == seriesId).ToListAsync(ct);
        return new OrganizePreview(series.Path, await FolderTargetAsync(series, media, ct), Plan(series, files, await EpisodesByFileAsync(seriesId, ct), media));
    }

    /// <summary>Applies the plan and returns a one-line summary for the command log.</summary>
    public async Task<string> OrganizeAsync(int seriesId, bool renameFolder, CancellationToken ct = default)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct) ?? throw new KeyNotFoundException($"Series {seriesId} not found");
        var media = await settings.GetAsync<MediaManagementSettings>(ct);
        var files = await db.EpisodeFiles.Where(f => f.SeriesId == seriesId).ToListAsync(ct);
        var fileIds = files.Select(f => f.Id).ToList();
        var subtitles = await db.SubtitleFiles.Where(s => s.EpisodeFileId != null && fileIds.Contains(s.EpisodeFileId.Value)).ToListAsync(ct);
        var problems = new List<string>();
        var summary = new List<string>();

        if (renameFolder && await FolderTargetAsync(series, media, ct) is { } target)
        {
            var oldFolder = series.Path;
            if (MoveSeriesFolder(series, target, subtitles, problems))
            {
                mediaServers.PathChanged(oldFolder);
                mediaServers.PathChanged(series.Path);
                summary.Add($"moved folder to {target}");
                await db.SaveChangesAsync(ct);
            }
        }

        var plan = Plan(series, files, await EpisodesByFileAsync(seriesId, ct), media);
        problems.AddRange(plan.Where(p => p.Problem is not null).Select(p => $"{p.Existing}: {p.Problem}"));
        var pending = plan.Where(p => p.Problem is null).ToList();
        var touchedDirs = new HashSet<string>(StringComparer.Ordinal);
        var renamed = 0;

        // Files can block each other (A → B while B → C), so retry the blocked ones while progress is made.
        while (pending.Count > 0)
        {
            var blocked = new List<OrganizeItem>();
            foreach (var item in pending)
            {
                var source = Path.Combine(series.Path, item.Existing);
                var destination = Path.Combine(series.Path, item.New);
                if (!File.Exists(source))
                {
                    problems.Add($"{item.Existing}: file not found");
                    continue;
                }

                if (File.Exists(destination) && !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                {
                    blocked.Add(item);
                    continue;
                }

                try
                {
                    MoveWithSubtitles(source, destination, subtitles);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{item.Existing}: {ex.Message}");
                    continue;
                }

                files.First(f => f.Id == item.EpisodeFileId).RelativePath = item.New;
                touchedDirs.Add(Path.GetDirectoryName(source)!);
                renamed++;
                logger.LogInformation("Organized {Source} to {Destination}", source, destination);
            }

            if (blocked.Count == pending.Count)
            {
                problems.AddRange(blocked.Select(b => $"{b.Existing}: {b.New} already exists"));
                break;
            }

            pending = blocked;
        }

        await db.SaveChangesAsync(ct);
        foreach (var dir in touchedDirs) LibraryFiles.DeleteEmptyFolders(series.Path, dir);
        if (renamed > 0) mediaServers.PathChanged(series.Path);
        await eventHub.PublishAsync(EventNames.SeriesUpdated, new { series.Id }, ct);

        summary.Insert(0, renamed == 0 && summary.Count == 0 && problems.Count == 0 ? "already organized" : $"renamed {renamed} file{(renamed == 1 ? "" : "s")}");
        return $"{series.Title}: {string.Join(", ", summary)}" + (problems.Count > 0 ? $" · skipped: {string.Join("; ", problems)}" : string.Empty);
    }

    /// <summary>Episode files whose relative path differs from the configured naming. Pure; unit tested.</summary>
    public static List<OrganizeItem> Plan(Core.Domain.Series series, IEnumerable<EpisodeFile> files, ILookup<int, Episode> episodesByFile, MediaManagementSettings media)
    {
        var expected = files
            .OrderBy(f => f.SeasonNumber).ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Select(f => (File: f, Path: ExpectedPath(series, f, episodesByFile[f.Id].ToList(), media)))
            .ToList();

        // Files already at their target keep it; the others claim theirs in order, so two files never get the same name.
        var claimed = expected.Where(x => x.Path is not null && string.Equals(x.Path, x.File.RelativePath, StringComparison.Ordinal))
            .Select(x => x.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<OrganizeItem>();
        foreach (var (file, path) in expected)
        {
            if (path is null)
                items.Add(new OrganizeItem(file.Id, file.RelativePath, file.RelativePath, "no episodes linked to this file"));
            else if (string.Equals(path, file.RelativePath, StringComparison.Ordinal))
                continue;
            else if (!claimed.Add(path))
                items.Add(new OrganizeItem(file.Id, file.RelativePath, path, "another file gets the same name"));
            else
                items.Add(new OrganizeItem(file.Id, file.RelativePath, path));
        }

        return items;
    }

    /// <summary>Relative path (season folder + file name) an episode file should have, or null without episodes.</summary>
    public static string? ExpectedPath(Core.Domain.Series series, EpisodeFile file, IReadOnlyList<Episode> episodes, MediaManagementSettings media)
    {
        if (episodes.Count == 0) return null;
        var extension = Path.GetExtension(file.RelativePath);
        var fileName = media.RenameFiles
            ? SeriesNaming.BuildEpisodeFileName(series, episodes, Qualities.FindById(file.QualityId), file.ReleaseGroup, media.EpisodeFileFormat, extension,
                MediaInfoSummary.Parse(file.MediaInfo))
            : Path.GetFileName(file.RelativePath);
        return series.SeasonFolder ? Path.Combine(SeriesNaming.BuildSeasonFolder(file.SeasonNumber, media.SeasonFolderFormat), fileName) : fileName;
    }

    private async Task<ILookup<int, Episode>> EpisodesByFileAsync(int seriesId, CancellationToken ct) =>
        (await db.Episodes.AsNoTracking()
            .Where(e => e.SeriesId == seriesId && e.EpisodeFileId != null)
            .Select(e => new Episode { Id = e.Id, SeriesId = e.SeriesId, SeasonNumber = e.SeasonNumber, EpisodeNumber = e.EpisodeNumber, Title = e.Title, EpisodeFileId = e.EpisodeFileId })
            .ToListAsync(ct))
        .ToLookup(e => e.EpisodeFileId!.Value);

    /// <summary>The folder the series should live in, when it differs from its current one and sits directly in a series root folder.</summary>
    private async Task<string?> FolderTargetAsync(Core.Domain.Series series, MediaManagementSettings media, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(MediaFolder.Normalize(series.Path));
        if (parent is null) return null;
        var roots = await db.RootFolders.AsNoTracking().Where(r => r.MediaType == MediaType.Series).Select(r => r.Path).ToListAsync(ct);
        if (!roots.Any(r => MediaFolder.SamePath(r, parent))) return null;
        try
        {
            var target = LibraryPaths.ResolveItemFolder(roots, roots.First(r => MediaFolder.SamePath(r, parent)), null, () => SeriesNaming.BuildSeriesFolder(series, media.SeriesFolderFormat));
            return string.Equals(target, MediaFolder.Normalize(series.Path), StringComparison.Ordinal) ? null : target;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private bool MoveSeriesFolder(Core.Domain.Series series, string target, List<SubtitleFile> subtitles, List<string> problems)
    {
        var current = MediaFolder.Normalize(series.Path);
        var caseOnly = string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
        {
            problems.Add($"folder {target} already exists");
            return false;
        }

        try
        {
            if (Directory.Exists(current)) Directory.Move(current, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"folder: {ex.Message}");
            return false;
        }

        var prefix = current + Path.DirectorySeparatorChar;
        foreach (var sub in subtitles.Where(s => s.Path.StartsWith(prefix, StringComparison.Ordinal)))
        {
            sub.Path = Path.Combine(target, sub.Path[prefix.Length..]);
        }

        logger.LogInformation("Moved series folder {Source} to {Destination}", current, target);
        series.Path = target;
        return true;
    }

    /// <summary>Moves a video and renames its subtitle files ("Old.en.srt" → "New.en.srt") alongside it.</summary>
    private static void MoveWithSubtitles(string source, string destination, List<SubtitleFile> subtitles)
    {
        var sidecars = SidecarFiles.SubtitlesFor(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);

        var oldBase = Path.GetFileNameWithoutExtension(source);
        var newBase = Path.GetFileNameWithoutExtension(destination);
        var newDir = Path.GetDirectoryName(destination)!;
        foreach (var sidecar in sidecars)
        {
            var target = Path.Combine(newDir, newBase + Path.GetFileName(sidecar)[oldBase.Length..]);
            if (File.Exists(target) && !string.Equals(target, sidecar, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Move(sidecar, target);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var row in subtitles.Where(s => string.Equals(s.Path, sidecar, StringComparison.Ordinal))) row.Path = target;
        }
    }
}
