using Krakarr.Media.Parsing;

namespace Krakarr.Media.Files;

/// <summary>Helpers for existing library folders.</summary>
public static class MediaFolder
{
    /// <summary>Video files below <paramref name="folder"/> (recursive), excluding samples, largest first.</summary>
    public static IReadOnlyList<(string Path, long Length)> VideoFiles(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        return Directory.EnumerateFiles(folder, "*", options)
            .Where(f => ReleaseParser.IsVideoFile(f) && !DownloadContents.IsSample(f))
            .Select(f => (f, new FileInfo(f).Length))
            .OrderByDescending(f => f.Length)
            .ToList();
    }

    /// <summary>Visible sub-folders of a root folder.</summary>
    public static IReadOnlyList<DirectoryInfo> SubFolders(string root)
    {
        if (!Directory.Exists(root)) return [];
        return new DirectoryInfo(root).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.') && (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when <paramref name="path"/> is an immediate child folder of <paramref name="root"/>.</summary>
    public static bool IsDirectChild(string root, string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), parent, StringComparison.Ordinal);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.Ordinal);
}
