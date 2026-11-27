namespace Krakarr.Media.Files;

/// <summary>Removal of video files that belong to a library item (a movie or series folder).</summary>
public static class LibraryFiles
{
    /// <summary>
    /// Deletes the video at <paramref name="relativePath"/> inside <paramref name="itemFolder"/> together with its subtitle
    /// sidecars, then removes folders left empty below the item folder (e.g. "Season 01"). A file that is already gone is
    /// not an error. Returns the absolute path of the video.
    /// </summary>
    /// <exception cref="InvalidOperationException">The path points outside the item folder.</exception>
    /// <exception cref="IOException">The file could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be deleted.</exception>
    public static string DeleteVideo(string itemFolder, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(itemFolder) || !Path.IsPathRooted(itemFolder) || string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidOperationException("The file has no valid path");

        var path = Path.GetFullPath(Path.Combine(itemFolder, relativePath));
        if (!MediaFolder.IsInside(itemFolder, path))
            throw new InvalidOperationException($"Refusing to delete {path}: it is not inside {itemFolder}");

        if (File.Exists(path)) File.Delete(path);
        SidecarFiles.DeleteFor(path);
        DeleteEmptyFolders(itemFolder, Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Removes <paramref name="dir"/> and its parents while empty, never the item folder itself or anything outside it.</summary>
    public static void DeleteEmptyFolders(string itemFolder, string dir)
    {
        var current = dir;
        while (MediaFolder.IsInside(itemFolder, current) && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            try
            {
                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            current = Path.GetDirectoryName(current)!;
        }
    }
}
