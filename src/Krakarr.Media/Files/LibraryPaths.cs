namespace Krakarr.Media.Files;

/// <summary>
/// Guards for library folders (movie and series folders). Every folder Krakarr manages must be a direct child of a
/// configured root folder, and files are only ever deleted strictly inside a root folder.
/// </summary>
public static class LibraryPaths
{
    /// <summary>
    /// Resolves the folder of a new library item: <paramref name="existingPath"/> as-is (library import), or
    /// <paramref name="rootFolderPath"/> combined with the name built by <paramref name="buildFolderName"/>.
    /// The result is normalized and must be an immediate child of one of <paramref name="roots"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder is not directly inside a configured root folder.</exception>
    public static string ResolveItemFolder(IReadOnlyCollection<string> roots, string? rootFolderPath, string? existingPath, Func<string> buildFolderName)
    {
        string candidate;
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            candidate = existingPath.Trim();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath))
                throw new InvalidOperationException("A root folder is required");
            if (!roots.Any(r => MediaFolder.SamePath(r, rootFolderPath)))
                throw new InvalidOperationException($"{rootFolderPath} is not a configured root folder");

            var name = buildFolderName();
            if (string.IsNullOrWhiteSpace(name) || name.Trim('.', ' ').Length == 0)
                throw new InvalidOperationException("The folder name built from the naming format is empty");
            candidate = Path.Combine(rootFolderPath, name);
        }

        if (!Path.IsPathRooted(candidate))
            throw new InvalidOperationException("An absolute path is required");

        var full = MediaFolder.Normalize(candidate);
        if (!roots.Any(r => MediaFolder.IsDirectChild(r, full)))
            throw new InvalidOperationException($"{full} is not a folder directly inside a configured root folder");
        return full;
    }

    /// <summary>True when <paramref name="path"/> may be deleted recursively: strictly inside a root folder, never a root itself.</summary>
    public static bool CanDeleteRecursively(IEnumerable<string> roots, string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && roots.Any(r => MediaFolder.IsInside(r, path));
}
