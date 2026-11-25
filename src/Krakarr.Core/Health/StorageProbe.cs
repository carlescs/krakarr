using System.Runtime.InteropServices;

namespace Krakarr.Core.Health;

/// <summary>Free and total bytes of the filesystem mounted at <see cref="MountPoint"/>.</summary>
public sealed record StorageSpace(string MountPoint, long FreeBytes, long TotalBytes);

/// <summary>
/// Cheap filesystem questions for health checks. None of them reads or writes files, so they don't wake sleeping
/// disks more than a directory listing would. An interface so checks can be tested without real mounts.
/// </summary>
public interface IStorageProbe
{
    bool DirectoryExists(string path);

    /// <summary>True when the directory has at least one file or sub-directory.</summary>
    bool HasEntries(string path);

    /// <summary>True when this process may create files in the directory (permissions and read-only mounts).</summary>
    bool IsWritable(string path);

    /// <summary>The filesystem holding <paramref name="path"/>, or null when unknown.</summary>
    StorageSpace? GetSpace(string path);
}

public sealed class StorageProbe : IStorageProbe
{
    private const int WriteAccess = 2; // W_OK

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool HasEntries(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool IsWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows reports read-only shares as access errors on write; not worth a probe file.
            return true;
        }

        // access(2) answers for this process's user and for read-only mounts (EROFS) without touching the disk.
        try
        {
            return Access(path, WriteAccess) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true; // can't tell; don't raise a false alarm
        }
    }

    public StorageSpace? GetSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var drive = MountPointOf(full, DriveInfo.GetDrives());
            if (drive is null) return null;
            return new StorageSpace(drive.RootDirectory.FullName, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The mount whose root is the longest prefix of <paramref name="fullPath"/>.</summary>
    internal static DriveInfo? MountPointOf(string fullPath, IEnumerable<DriveInfo> drives)
    {
        DriveInfo? best = null;
        var bestLength = -1;
        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;
            if (IsUnder(fullPath, root) && root.Length > bestLength)
            {
                best = drive;
                bestLength = root.Length;
            }
        }

        return best;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it (segment-wise).</summary>
    public static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmedRoot = root.TrimEnd('/', '\\');
        if (trimmedRoot.Length == 0) return path.StartsWith('/'); // "/" holds every absolute path
        if (!path.StartsWith(trimmedRoot, comparison)) return false;
        return path.Length == trimmedRoot.Length || path[trimmedRoot.Length] is '/' or '\\';
    }

    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
