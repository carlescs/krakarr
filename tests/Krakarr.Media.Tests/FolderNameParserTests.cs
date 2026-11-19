using Krakarr.Media.Files;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class FolderNameParserTests
{
    [Theory]
    [InlineData("The Matrix (1999)", "The Matrix", 1999)]
    [InlineData("Amélie [2001]", "Amélie", 2001)]
    [InlineData("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968)]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS", "The Matrix", 1999)]
    [InlineData("Breaking Bad", "Breaking Bad", null)]
    [InlineData("Doctor_Who", "Doctor Who", null)]
    [InlineData("Spider-Man: No Way Home (2021)", "Spider-Man: No Way Home", 2021)]
    public void Parses_title_and_year(string folder, string title, int? year)
    {
        var p = FolderNameParser.Parse(folder);
        Assert.Equal(title, p.Title);
        Assert.Equal(year, p.Year);
        Assert.False(p.HasId);
    }

    [Theory]
    [InlineData("The Matrix (1999) {tmdb-603}", 603, null, null)]
    [InlineData("The Matrix (1999) [tmdbid-603]", 603, null, null)]
    [InlineData("The Matrix (1999) {imdb-tt0133093}", null, "tt0133093", null)]
    [InlineData("The Matrix [imdbid-0133093] (1999)", null, "tt0133093", null)]
    [InlineData("Breaking Bad {tvdb-81189}", null, null, 81189)]
    [InlineData("Breaking Bad (2008) [tvdbid-81189]", null, null, 81189)]
    public void Reads_id_tags(string folder, int? tmdb, string? imdb, int? tvdb)
    {
        var p = FolderNameParser.Parse(folder);
        Assert.Equal(tmdb, p.TmdbId);
        Assert.Equal(imdb, p.ImdbId);
        Assert.Equal(tvdb, p.TvdbId);
        Assert.DoesNotContain("{", p.Title);
        Assert.DoesNotContain("[", p.Title);
    }

    [Fact]
    public void Id_tag_is_removed_from_title()
    {
        var p = FolderNameParser.Parse("The Matrix (1999) {tmdb-603}");
        Assert.Equal("The Matrix", p.Title);
        Assert.Equal(1999, p.Year);
    }

    [Fact]
    public void Lists_video_files_and_checks_paths()
    {
        var root = Directory.CreateTempSubdirectory("krakarr-folder");
        try
        {
            var movie = Directory.CreateDirectory(Path.Combine(root.FullName, "Movie (2020)"));
            File.WriteAllBytes(Path.Combine(movie.FullName, "movie.mkv"), new byte[2000]);
            Directory.CreateDirectory(Path.Combine(movie.FullName, "Sample"));
            File.WriteAllBytes(Path.Combine(movie.FullName, "Sample", "movie.sample.mkv"), new byte[10]);
            File.WriteAllText(Path.Combine(movie.FullName, "movie.nfo"), "x");
            Directory.CreateDirectory(Path.Combine(root.FullName, ".hidden"));

            Assert.Equal(["movie.mkv"], MediaFolder.VideoFiles(movie.FullName).Select(f => Path.GetFileName(f.Path)));
            Assert.Equal(["Movie (2020)"], MediaFolder.SubFolders(root.FullName).Select(d => d.Name));
            Assert.True(MediaFolder.IsDirectChild(root.FullName, movie.FullName));
            Assert.True(MediaFolder.IsDirectChild(root.FullName + "/", movie.FullName + "/"));
            Assert.False(MediaFolder.IsDirectChild(root.FullName, Path.Combine(movie.FullName, "Sample")));
            Assert.False(MediaFolder.IsDirectChild(root.FullName, Path.Combine(root.FullName, "..", "etc")));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
