namespace Krakarr.Media.Files;

/// <summary>Files that belong to a video and share its base name (subtitles).</summary>
public static class SidecarFiles
{
    public static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".sup"];

    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Subtitle files next to <paramref name="videoPath"/> whose name starts with the video's base name.</summary>
    public static IReadOnlyList<string> SubtitlesFor(string videoPath)
    {
        var dir = Path.GetDirectoryName(videoPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return [];
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Directory.EnumerateFiles(dir)
            .Where(f => IsSubtitle(f) && Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Deletes the subtitle files of a video (used when the video is replaced or removed).</summary>
    public static void DeleteFor(string videoPath)
    {
        foreach (var file in SubtitlesFor(videoPath))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
