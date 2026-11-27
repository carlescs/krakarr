using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public class LibraryPathsTests
{
    private static readonly string[] Roots = ["/data/media/movies", "/data/media/tv/"];

    private static string Rooted(params string[] segments) =>
        Path.Combine([Path.GetPathRoot(Environment.CurrentDirectory)!, .. segments]);

    [Fact]
    public void Builds_folder_inside_a_configured_root()
    {
        var path = LibraryPaths.ResolveItemFolder(Roots, "/data/media/movies", null, () => "The Matrix (1999)");
        Assert.Equal(Rooted("data", "media", "movies", "The Matrix (1999)"), path);
    }

    [Fact]
    public void Accepts_existing_folder_directly_inside_a_root_and_normalizes_it()
    {
        Assert.Equal(Rooted("data", "media", "tv", "Show"), LibraryPaths.ResolveItemFolder(Roots, null, "/data/media/tv/Show/", () => "unused"));
        Assert.Equal(Rooted("data", "media", "tv", "Show"), LibraryPaths.ResolveItemFolder(Roots, null, "/data/media/movies/../tv/Show", () => "unused"));
    }

    [Theory]
    [InlineData("/config")]
    [InlineData("/")]
    [InlineData("/data")]
    [InlineData("/data/media/movies")]                 // the root itself
    [InlineData("/data/media/movies/A/B")]             // not a direct child
    [InlineData("/data/media/movies/../../../config")] // traversal
    [InlineData("relative/path")]
    public void Rejects_existing_paths_outside_roots(string existing)
    {
        Assert.Throws<InvalidOperationException>(() => LibraryPaths.ResolveItemFolder(Roots, "/data/media/movies", existing, () => "x"));
    }

    [Fact]
    public void Rejects_unconfigured_root_folder()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LibraryPaths.ResolveItemFolder(Roots, "/", null, () => "config"));
        Assert.Contains("not a configured root folder", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(". .")]
    public void Rejects_folder_names_that_resolve_to_the_root(string name)
    {
        Assert.Throws<InvalidOperationException>(() => LibraryPaths.ResolveItemFolder(Roots, "/data/media/movies", null, () => name));
    }

    [Theory]
    [InlineData("/data/media/movies/The Matrix (1999)", true)]
    [InlineData("/data/media/tv/Show/Season 01", true)]
    [InlineData("/data/media/movies", false)]
    [InlineData("/data/media/movies/", false)]
    [InlineData("/data/media/movies/..", false)]
    [InlineData("/data/media/moviesX/thing", false)]
    [InlineData("/config", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void Recursive_delete_only_strictly_inside_a_root(string path, bool allowed)
    {
        Assert.Equal(allowed, LibraryPaths.CanDeleteRecursively(Roots, path));
    }

    [Fact]
    public void Root_slash_is_handled()
    {
        Assert.True(MediaFolder.IsInside("/", "/data"));
        Assert.False(MediaFolder.IsInside("/", "/"));
    }
}
