using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public sealed class LibraryFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("krakarr-files").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(params string[] segments)
    {
        var path = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Deletes_the_video_its_subtitles_and_the_emptied_season_folder()
    {
        var show = Path.Combine(_root, "Show");
        var video = Touch("Show", "Season 01", "Show - S01E01.mkv");
        var subtitle = Touch("Show", "Season 01", "Show - S01E01.en.srt");
        var other = Touch("Show", "Season 02", "Show - S02E01.mkv");

        Assert.Equal(video, LibraryFiles.DeleteVideo(show, Path.Combine("Season 01", "Show - S01E01.mkv")));

        Assert.False(File.Exists(video));
        Assert.False(File.Exists(subtitle));
        Assert.False(Directory.Exists(Path.Combine(show, "Season 01")));
        Assert.True(File.Exists(other));
        Assert.True(Directory.Exists(show)); // the item folder itself stays
    }

    [Fact]
    public void Keeps_the_item_folder_and_tolerates_files_already_gone()
    {
        var movie = Path.Combine(_root, "Movie (2000)");
        Touch("Movie (2000)", "Movie (2000).mkv");

        LibraryFiles.DeleteVideo(movie, "Movie (2000).mkv");
        LibraryFiles.DeleteVideo(movie, "Movie (2000).mkv");

        Assert.True(Directory.Exists(movie));
    }

    [Theory]
    [InlineData("../Other/file.mkv")]
    [InlineData("..")]
    [InlineData("")]
    public void Refuses_paths_outside_the_item_folder(string relative)
    {
        var victim = Touch("Other", "file.mkv");
        Directory.CreateDirectory(Path.Combine(_root, "Movie"));

        Assert.Throws<InvalidOperationException>(() => LibraryFiles.DeleteVideo(Path.Combine(_root, "Movie"), relative));
        Assert.True(File.Exists(victim));
    }
}
