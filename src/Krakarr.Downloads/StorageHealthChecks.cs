using System.Globalization;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Health;
using Krakarr.Data;
using Krakarr.Downloads.QBittorrent;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Downloads;

/// <summary>Where finished downloads land, as Krakarr sees it.</summary>
public static class DownloadFolder
{
    /// <summary>
    /// The built-in engine's download directory, or qBittorrent's save path mapped to a local path. Null when it can't
    /// be known (qBittorrent with its own default save path and no path mapping).
    /// </summary>
    public static string? Resolve(DownloadClientSettings settings, KrakarrPaths paths)
    {
        if (settings.Client == DownloadClientKind.Embedded)
        {
            return string.IsNullOrWhiteSpace(settings.DownloadDirectory) ? paths.DefaultDownloadDirectory : settings.DownloadDirectory.Trim();
        }

        var qb = settings.QBittorrent;
        if (!string.IsNullOrWhiteSpace(qb.SavePath)) return QBittorrentEngine.MapPath(qb.SavePath.Trim(), qb);
        return string.IsNullOrWhiteSpace(qb.LocalPathPrefix) ? null : qb.LocalPathPrefix.Trim();
    }
}

internal static class Sizes
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 || value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

/// <summary>Root folders that are missing, read-only, or look unmounted (empty while the library has files there).</summary>
public sealed class RootFolderHealthCheck(KrakarrDbContext db, IStorageProbe storage) : IHealthCheck
{
    public string Id => "rootfolders";
    public string Name => "Root folders";

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var roots = await db.RootFolders.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        var problems = new List<HealthProblem>();
        foreach (var root in roots)
        {
            var key = root.Id.ToString(CultureInfo.InvariantCulture);
            if (!storage.DirectoryExists(root.Path))
            {
                problems.Add(new HealthProblem(key, HealthSeverity.Error,
                    $"Root folder {root.Path} is missing. Is the drive or network share mounted (and the volume mapped into the container)?", "/settings/media"));
            }
            else if (!storage.IsWritable(root.Path))
            {
                problems.Add(new HealthProblem(key, HealthSeverity.Error,
                    $"Root folder {root.Path} is read-only for Krakarr, so imports into it fail. Check the mount options and folder permissions.", "/settings/media"));
            }
            else if (!storage.HasEntries(root.Path) && await TitlesWithFilesAsync(root, ct) is var titles and > 0)
            {
                problems.Add(new HealthProblem(key, HealthSeverity.Error,
                    $"Root folder {root.Path} is empty, but {titles} title(s) in the library have files there. Is the drive or network share mounted?", "/settings/media"));
            }
        }

        return problems;
    }

    /// <summary>Library titles with files below the root folder (only asked when the folder is empty).</summary>
    private async Task<int> TitlesWithFilesAsync(RootFolder root, CancellationToken ct)
    {
        var prefix = root.Path.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return root.MediaType == MediaType.Movie
            ? await db.Movies.AsNoTracking().CountAsync(m => m.MovieFileId != null && m.Path.StartsWith(prefix), ct)
            : await db.Series.AsNoTracking().CountAsync(s => s.Path.StartsWith(prefix) && db.EpisodeFiles.Any(f => f.SeriesId == s.Id), ct);
    }
}

/// <summary>Low free space on the filesystems holding the root folders and the download folder (one problem per filesystem).</summary>
public sealed class DiskSpaceHealthCheck(KrakarrDbContext db, ISettingsService settings, KrakarrPaths paths, IStorageProbe storage) : IHealthCheck
{
    /// <summary>Warn below 10 % free, but never above 50 GB (10 % of a big NAS is plenty).</summary>
    public const double WarningFraction = 0.10;
    public const long WarningCapBytes = 50L * 1024 * 1024 * 1024;
    /// <summary>Error below 2 % free, but never above 5 GB: a single download may no longer fit.</summary>
    public const double ErrorFraction = 0.02;
    public const long ErrorCapBytes = 5L * 1024 * 1024 * 1024;

    public string Id => "diskspace";
    public string Name => "Disk space";

    /// <summary>How worrying <paramref name="free"/> bytes out of <paramref name="total"/> are; null when fine.</summary>
    public static HealthSeverity? SeverityFor(long free, long total)
    {
        if (total <= 0) return null;
        if (free < Math.Min((long)(total * ErrorFraction), ErrorCapBytes)) return HealthSeverity.Error;
        if (free < Math.Min((long)(total * WarningFraction), WarningCapBytes)) return HealthSeverity.Warning;
        return null;
    }

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var folders = await db.RootFolders.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Path, r.MediaType }).ToListAsync(ct);
        var used = folders.Select(r => (r.Path, Label: $"{(r.MediaType == MediaType.Movie ? "movies" : "series")} root folder {r.Path}")).ToList();
        if (DownloadFolder.Resolve(await settings.GetAsync<DownloadClientSettings>(ct), paths) is { } downloads)
        {
            used.Add((downloads, $"downloads {downloads}"));
        }

        var problems = new List<HealthProblem>();
        foreach (var mount in used
                     .Where(u => storage.DirectoryExists(u.Path))
                     .Select(u => (u.Label, Space: storage.GetSpace(u.Path)))
                     .Where(u => u.Space is not null)
                     .GroupBy(u => u.Space!.MountPoint))
        {
            var space = mount.First().Space!;
            if (SeverityFor(space.FreeBytes, space.TotalBytes) is not { } severity) continue;
            problems.Add(new HealthProblem(mount.Key, severity,
                $"Only {Sizes.Format(space.FreeBytes)} of {Sizes.Format(space.TotalBytes)} free on {mount.Key} ({string.Join(", ", mount.Select(m => m.Label))}).",
                "/settings/media"));
        }

        return problems;
    }
}

/// <summary>
/// The download folder: missing or (for the built-in engine) read-only, and, with hardlinks on, on another filesystem
/// than a root folder, where imports silently copy (twice the space) instead of hardlinking.
/// </summary>
public sealed class DownloadFolderHealthCheck(KrakarrDbContext db, ISettingsService settings, KrakarrPaths paths, IStorageProbe storage) : IHealthCheck
{
    public string Id => "downloadfolder";
    public string Name => "Download folder";

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var client = await settings.GetAsync<DownloadClientSettings>(ct);
        if (DownloadFolder.Resolve(client, paths) is not { } downloads) return [];

        if (!storage.DirectoryExists(downloads))
        {
            // qBittorrent creates its save path when it first saves there; only the mapped path can be wrong.
            return client.Client == DownloadClientKind.Embedded
                ? [new HealthProblem("missing", HealthSeverity.Error, $"The download folder {downloads} is missing. Is the drive or volume mounted?", "/settings/downloads")]
                : [new HealthProblem("missing", HealthSeverity.Warning,
                    $"qBittorrent's download folder is {downloads} as Krakarr sees it, but it doesn't exist here. Check the save path and the remote path mapping.", "/settings/downloads")];
        }

        var problems = new List<HealthProblem>();
        if (client.Client == DownloadClientKind.Embedded && !storage.IsWritable(downloads))
        {
            problems.Add(new HealthProblem("readonly", HealthSeverity.Error,
                $"The download folder {downloads} is read-only for Krakarr, so torrents can't be downloaded. Check the mount options and folder permissions.", "/settings/downloads"));
        }

        var media = await settings.GetAsync<MediaManagementSettings>(ct);
        if (media.UseHardlinks && storage.GetSpace(downloads)?.MountPoint is { } downloadMount)
        {
            var roots = await db.RootFolders.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
            foreach (var root in roots.Where(r => storage.DirectoryExists(r.Path)))
            {
                if (storage.GetSpace(root.Path)?.MountPoint is not { } rootMount || rootMount == downloadMount) continue;
                problems.Add(new HealthProblem($"hardlink.{root.Id}", HealthSeverity.Warning,
                    $"Downloads ({downloads}, on {downloadMount}) and root folder {root.Path} (on {rootMount}) are on different filesystems, so imports copy "
                    + "files instead of hardlinking them and use twice the space. Mount both from one volume (e.g. /data/downloads and /data/media).",
                    "/settings/media"));
            }
        }

        return problems;
    }
}
