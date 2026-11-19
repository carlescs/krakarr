using System.Text.RegularExpressions;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Files;

/// <summary>Locates the video files of a completed download.</summary>
public static partial class DownloadContents
{
    /// <summary>
    /// Returns the existing, non-sample video files of a download, largest first. Uses the file list reported by the
    /// download client and falls back to scanning <c>{savePath}/{name}</c> when the client knows nothing about it.
    /// </summary>
    public static IReadOnlyList<(string Path, long Length)> VideoFiles(
        IEnumerable<(string Path, long Length)> clientFiles, string savePath, string name)
    {
        var files = clientFiles.ToList();
        if (files.Count == 0 && !string.IsNullOrEmpty(savePath))
        {
            var root = Path.Combine(savePath, name);
            if (File.Exists(root))
            {
                files = [(root, new FileInfo(root).Length)];
            }
            else if (Directory.Exists(root))
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length)).ToList();
            }
        }

        return files
            .Where(f => ReleaseParser.IsVideoFile(f.Path) && File.Exists(f.Path))
            .Where(f => !IsSample(f.Path))
            .OrderByDescending(f => f.Length)
            .ToList();
    }

    public static bool IsSample(string path) =>
        SampleRegex().IsMatch(Path.GetFileNameWithoutExtension(path))
        || Path.GetDirectoryName(path)?.Split(Path.DirectorySeparatorChar, '/').Any(d => d.Equals("sample", StringComparison.OrdinalIgnoreCase)) == true;

    [GeneratedRegex(@"(?<![a-z])sample(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SampleRegex();
}
