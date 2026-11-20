using Krakarr.Core.Domain;
using static Krakarr.Series.Tests.TestData;

namespace Krakarr.Series.Tests;

public class SeriesNamingTests
{
    [Fact]
    public void Builds_folders_and_file_names()
    {
        var s = new Core.Domain.Series { Title = "Breaking Bad", Year = 2008, Path = "/tv", TmdbId = 1396, TvdbId = 81189 };
        Assert.Equal("Breaking Bad (2008)", SeriesNaming.BuildSeriesFolder(s, "{Series Title} ({Series Year})"));
        Assert.Equal("Season 03", SeriesNaming.BuildSeasonFolder(3, "Season {Season}"));
        Assert.Equal("Specials", SeriesNaming.BuildSeasonFolder(0, "Season {Season}"));

        var eps = new List<Episode>
        {
            new() { SeasonNumber = 3, EpisodeNumber = 7, Title = "One Minute" },
            new() { SeasonNumber = 3, EpisodeNumber = 6, Title = "Sunset" },
        };
        Assert.Equal("Breaking Bad - S03E06-E07 - Sunset + One Minute [Bluray-1080p].mkv",
            SeriesNaming.BuildEpisodeFileName(s, eps, Qualities.Bluray1080, "GRP", "{Series Title} - S{Season}E{Episode} - {Episode Title} [{Quality}]", ".MKV"));
    }

    [Fact]
    public void Does_not_repeat_year_already_in_title()
    {
        var s = new Core.Domain.Series { Title = "Doctor Who (2005)", Year = 2005, Path = "/tv" };
        Assert.Equal("Doctor Who (2005)", SeriesNaming.BuildSeriesFolder(s, "{Series Title} ({Series Year})"));
        Assert.Equal("Doctor Who", SeriesNaming.BaseTitle(s.Title));
    }

    [Fact]
    public void Formats_episode_codes()
    {
        Assert.Equal("S01E02E03", SeriesNaming.EpisodeCode(1, [3, 2]));
        var s = Office();
        Assert.Equal("S01E01E02, S02E01", SeriesService.Codes(s.Episodes.Where(e => (e.SeasonNumber, e.EpisodeNumber) is (1, 1) or (1, 2) or (2, 1))));
    }

    [Theory]
    [InlineData(MonitorMode.All, new[] { "1x1", "1x5", "2x1" }, new string[0])]
    [InlineData(MonitorMode.Future, new[] { "1x5" }, new[] { "1x1", "2x1" })]
    [InlineData(MonitorMode.FirstSeason, new[] { "1x1", "1x5" }, new[] { "2x1" })]
    [InlineData(MonitorMode.LatestSeason, new[] { "2x1" }, new[] { "1x1" })]
    [InlineData(MonitorMode.None, new string[0], new[] { "1x1", "2x1" })]
    public void Applies_monitor_modes(MonitorMode mode, string[] monitored, string[] unmonitored)
    {
        var s = Office();
        SeriesService.ApplyMonitorMode(s, mode, Today);
        Episode E(string code) => s.Episodes.Single(e => $"{e.SeasonNumber}x{e.EpisodeNumber}" == code);
        Assert.All(monitored, c => Assert.True(E(c).Monitored, c));
        Assert.All(unmonitored, c => Assert.False(E(c).Monitored, c));
        Assert.False(s.Episodes.Single(e => e.SeasonNumber == 0).Monitored); // specials never auto-monitored
        Assert.False(s.Seasons.Single(x => x.SeasonNumber == 0).Monitored);
    }
}
