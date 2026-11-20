using Krakarr.Core.Domain;
using static Krakarr.Series.Tests.TestData;

namespace Krakarr.Series.Tests;

public class EpisodeDecisionEngineTests
{
    private static EpisodeDecision Eval(Core.Domain.Series s, string title, SearchScope? scope = null, bool requireMonitored = true, long sizeMb = 1500) =>
        EpisodeDecisionEngine.Evaluate(s, Hd1080, s.Episodes, Release(title, sizeMb: sizeMb), scope, requireMonitored, Today);

    [Fact]
    public void Maps_single_and_multi_episode_releases()
    {
        var s = Office();
        var single = Eval(s, "The.Office.US.S01E02.1080p.WEB-DL.DD5.1.H.264-NTb");
        // "The Office US" is a different show from "The Office".
        Assert.Contains(single.Rejections, r => r.StartsWith("Series mismatch"));

        var ok = Eval(s, "The.Office.S01E02E03.1080p.WEB-DL.DD5.1.H.264-NTb");
        Assert.True(ok.Approved, string.Join(", ", ok.Rejections));
        Assert.Equal([2, 3], ok.Episodes.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public void Country_suffix_in_title_matches_scene_naming()
    {
        var s = Office();
        s.Title = "The Office (US)";
        Assert.True(Eval(s, "The.Office.US.S01E02.1080p.WEB-DL.DD5.1.H.264-NTb").Approved);
        Assert.Contains(Eval(s, "The.Office.S01E02.1080p.WEB-DL-GRP").Rejections, r => r.StartsWith("Series mismatch"));
    }

    [Fact]
    public void Season_pack_covers_only_aired_episodes()
    {
        var d = Eval(Office(), "The.Office.S01.1080p.BluRay.x264-GRP", sizeMb: 6000);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
        Assert.True(d.IsSeasonPack);
        Assert.Equal([1, 2, 3, 4], d.Episodes.Select(e => e.EpisodeNumber));
    }

    [Fact]
    public void Matches_by_tvdb_id_even_when_title_differs()
    {
        var release = Release("Office.Show.S02E01.1080p.WEB-DL-GRP") with { TvdbId = 73244 };
        var d = EpisodeDecisionEngine.Evaluate(Office(), Hd1080, Office().Episodes, release, null, true, Today);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
    }

    [Fact]
    public void Matches_by_tmdb_id_even_when_title_differs()
    {
        var release = Release("Office.Show.S02E01.1080p.WEB-DL-GRP") with { TmdbId = 2316 };
        var d = EpisodeDecisionEngine.Evaluate(Office(), Hd1080, Office().Episodes, release, null, true, Today);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
    }

    [Theory]
    [InlineData("The.Office.S03E01.1080p.WEB-DL-GRP", "Unknown episode S03E01")]
    [InlineData("The.Office.S01E02.720p.HDTV.x264-GRP", "not wanted")]
    [InlineData("Parks.and.Recreation.S01E02.1080p.WEB-DL-GRP", "Series mismatch")]
    [InlineData("The.Office.2019.S01E02.1080p.WEB-DL-GRP", "Year mismatch")]
    [InlineData("The.Office.1080p.WEB-DL-GRP", "Not a TV")]
    [InlineData("The.Office.S00E01.1080p.WEB-DL-GRP", "Not monitored: S00E01")]
    public void Rejects(string title, string reason)
    {
        var d = Eval(Office(), title);
        Assert.False(d.Approved);
        Assert.Contains(d.Rejections, r => r.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scope_restricts_season_and_episode()
    {
        var s = Office();
        var e3 = s.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 3);
        Assert.Contains(Eval(s, "The.Office.S01E02.1080p.WEB-DL-GRP", new SearchScope(1, new HashSet<int> { e3.Id })).Rejections,
            r => r.Contains("requested episode"));
        Assert.Contains(Eval(s, "The.Office.S02E01.1080p.WEB-DL-GRP", new SearchScope(1, null)).Rejections, r => r.StartsWith("Wrong season"));
        Assert.True(Eval(s, "The.Office.S01.1080p.WEB-DL-GRP", new SearchScope(1, new HashSet<int> { e3.Id }), sizeMb: 6000).Approved);
    }

    [Fact]
    public void Size_limits_are_per_episode()
    {
        var profile = Hd1080.with_min(500);
        var s = Office();
        var pack = EpisodeDecisionEngine.Evaluate(s, profile, s.Episodes, Release("The.Office.S01.1080p.WEB-DL-GRP", sizeMb: 1600), null, true, Today);
        Assert.Contains(pack.Rejections, r => r.StartsWith("Too small"));
        var bigPack = EpisodeDecisionEngine.Evaluate(s, profile, s.Episodes, Release("The.Office.S01.1080p.WEB-DL-GRP", sizeMb: 4000), null, true, Today);
        Assert.True(bigPack.Approved, string.Join(", ", bigPack.Rejections));
    }

    [Fact]
    public void Upgrades_must_improve_every_episode_with_a_file()
    {
        var s = Office();
        var e1 = s.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        e1.EpisodeFile = new EpisodeFile { Id = 9, SeriesId = 1, RelativePath = "x.mkv", QualityId = Qualities.WebDl1080.Id };
        e1.EpisodeFileId = 9;

        Assert.Contains(Eval(s, "The.Office.S01E01.1080p.WEB-DL-GRP").Rejections, r => r.StartsWith("Not an upgrade for S01E01"));
        Assert.True(Eval(s, "The.Office.S01E01.1080p.BluRay.x264-GRP").Approved);
        Assert.Contains(Eval(s, "The.Office.S01.1080p.WEB-DL-GRP", sizeMb: 6000).Rejections, r => r.StartsWith("Not an upgrade for S01E01"));

        e1.EpisodeFile.QualityId = Qualities.Bluray1080.Id;
        Assert.Contains(Eval(s, "The.Office.S01E01.1080p.BluRay.x264-GRP").Rejections, r => r.Contains("meets cutoff"));
    }

    [Fact]
    public void Interactive_search_ignores_monitoring()
    {
        var d = Eval(Office(), "The.Office.S00E01.1080p.WEB-DL-GRP", requireMonitored: false);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
    }

    [Fact]
    public void Select_grabs_prefers_packs_and_never_overlaps()
    {
        var s = Office();
        var ranked = EpisodeDecisionEngine.Evaluate(s, Hd1080, s.Episodes,
        [
            Release("The.Office.S01E01.1080p.WEB-DL-A", seeders: 500),
            Release("The.Office.S01.1080p.WEB-DL-PACK", seeders: 5, sizeMb: 6000),
            Release("The.Office.S01E02.1080p.BluRay.x264-B", seeders: 5),
            Release("The.Office.S02E02.1080p.WEB-DL-C", seeders: 5),
        ], null, true, Today);

        var picked = EpisodeDecisionEngine.SelectGrabs(ranked, alreadyCovered: [s.Episodes.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 2).Id]);

        // The better-quality single episode wins its episode, which blocks the overlapping pack;
        // then the remaining single episode fills in. S02E02 is already downloading.
        Assert.Equal(["The.Office.S01E02.1080p.BluRay.x264-B", "The.Office.S01E01.1080p.WEB-DL-A"], picked.Select(p => p.Release.Title));
    }

    [Fact]
    public void Wanted_means_monitored_aired_and_missing_or_below_cutoff()
    {
        var s = Office();
        var future = s.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 5);
        var special = s.Episodes.Single(e => e.SeasonNumber == 0);
        var e1 = s.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        Assert.True(EpisodeDecisionEngine.IsWanted(e1, Hd1080, Today));
        Assert.False(EpisodeDecisionEngine.IsWanted(future, Hd1080, Today));
        Assert.False(EpisodeDecisionEngine.IsWanted(special, Hd1080, Today));
        e1.EpisodeFile = new EpisodeFile { RelativePath = "x", QualityId = Qualities.Bluray1080.Id };
        Assert.False(EpisodeDecisionEngine.IsWanted(e1, Hd1080, Today));
    }
}

internal static class ProfileExtensions
{
    // Tiny helper so tests read naturally: Hd1080 with_min(500)
    public static QualityProfile with_min(this QualityProfile p, long minMb) => new()
    {
        Id = p.Id, Name = p.Name, AllowedQualityIds = p.AllowedQualityIds, CutoffQualityId = p.CutoffQualityId, UpgradeAllowed = p.UpgradeAllowed, MinSizeMb = minMb,
    };
}
