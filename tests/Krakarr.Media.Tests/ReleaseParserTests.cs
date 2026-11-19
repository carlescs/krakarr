using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class ReleaseParserTests
{
    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS", "The Matrix", 1999)]
    [InlineData("Blade Runner 2049 (2017) 2160p UHD BluRay REMUX HDR HEVC-FGT", "Blade Runner 2049", 2017)]
    [InlineData("2001.A.Space.Odyssey.1968.720p.BluRay.x264-AMIABLE", "2001 A Space Odyssey", 1968)]
    [InlineData("Amélie.2001.1080p.WEB-DL.DD5.1.H264-FGT", "Amélie", 2001)]
    [InlineData("Dune.Part.Two.2024.1080p.AMZN.WEB-DL.DDP5.1.H.264-FLUX.mkv", "Dune Part Two", 2024)]
    [InlineData("[Group] Movie Name (2010) [1080p]", "Movie Name", 2010)]
    public void Parses_movie_title_and_year(string release, string title, int year)
    {
        var parsed = ReleaseParser.Parse(release);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.False(parsed.IsEpisode);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS", "Bluray-1080p")]
    [InlineData("Movie.2020.2160p.UHD.BluRay.REMUX.HDR.HEVC-FGT", "Remux-2160p")]
    [InlineData("Movie.2020.1080p.BluRay.REMUX.AVC-FGT", "Remux-1080p")]
    [InlineData("Movie.2020.1080p.WEB-DL.DDP5.1.H.264-FLUX", "WEBDL-1080p")]
    [InlineData("Movie.2020.1080p.WEBRip.x264-RARBG", "WEBRip-1080p")]
    [InlineData("Movie.2020.720p.HDTV.x264-GRP", "HDTV-720p")]
    [InlineData("Movie.2020.HDTV.x264-GRP", "SDTV")]
    [InlineData("Movie.2020.DVDRip.XviD-GRP", "DVD")]
    [InlineData("Movie.2020.HDCAM.x264-GRP", "CAM")]
    [InlineData("Movie.2020.HDTS.x264-GRP", "TELESYNC")]
    [InlineData("Movie.2020.2160p.WEB.h265-GRP", "WEBDL-2160p")]
    [InlineData("Movie.2020.1080p.x264-GRP", "WEBDL-1080p")]
    [InlineData("Movie.2020.x264-GRP", "Unknown")]
    public void Parses_quality(string release, string quality)
    {
        Assert.Equal(quality, ReleaseParser.Parse(release).Quality.Name);
    }

    [Theory]
    [InlineData("The.Matrix.1999.1080p.BluRay.x264-SPARKS", "SPARKS")]
    [InlineData("Movie.2020.1080p.WEB-DL.DDP5.1.H.264-FLUX.mkv", "FLUX")]
    [InlineData("Movie 2020 1080p WEB-DL", null)]
    [InlineData("Movie.2020.1080p.WEB-DL.x264", null)]
    public void Parses_release_group(string release, string? group)
    {
        Assert.Equal(group, ReleaseParser.Parse(release).ReleaseGroup);
    }

    [Fact]
    public void Parses_single_episode()
    {
        var parsed = ReleaseParser.Parse("The.Office.US.S05E14.720p.WEB-DL.DD5.1.H.264-NTb");
        Assert.Equal("The Office US", parsed.Title);
        Assert.Equal(5, parsed.SeasonNumber);
        Assert.Equal([14], parsed.EpisodeNumbers);
        Assert.Equal(Qualities.WebDl720, parsed.Quality);
    }

    [Fact]
    public void Parses_multi_episode()
    {
        var parsed = ReleaseParser.Parse("Show.Name.S01E01E02.1080p.HDTV.x264-GRP");
        Assert.Equal([1, 2], parsed.EpisodeNumbers);
    }

    [Fact]
    public void Parses_episode_range()
    {
        var parsed = ReleaseParser.Parse("Show.Name.S02E01-E04.1080p.WEB.h264-GRP");
        Assert.Equal([1, 2, 3, 4], parsed.EpisodeNumbers);
    }

    [Fact]
    public void Parses_season_pack()
    {
        var parsed = ReleaseParser.Parse("Show.Name.2019.S03.1080p.BluRay.x264-GRP");
        Assert.True(parsed.IsSeasonPack);
        Assert.Equal(3, parsed.SeasonNumber);
        Assert.Equal("Show Name", parsed.Title);
        Assert.Equal(2019, parsed.Year);
    }

    [Fact]
    public void Detects_proper_and_repack()
    {
        Assert.True(ReleaseParser.Parse("Movie.2020.PROPER.1080p.WEB-DL-GRP").IsProper);
        Assert.True(ReleaseParser.Parse("Movie.2020.REPACK.1080p.WEB-DL-GRP").IsRepack);
    }

    [Theory]
    [InlineData("The Matrix", "matrix")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Fast & Furious", "fastandfurious")]
    [InlineData("Spider-Man: No Way Home", "spidermannowayhome")]
    [InlineData("Them", "them")]
    public void Normalizes_titles(string input, string expected)
    {
        Assert.Equal(expected, ReleaseParser.NormalizeTitle(input));
    }
}
