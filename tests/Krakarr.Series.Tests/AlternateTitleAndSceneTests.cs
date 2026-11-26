using System.Text.Json;
using Krakarr.Core.Domain;
using static Krakarr.Series.Tests.TestData;

namespace Krakarr.Series.Tests;

public class AlternateTitleAndSceneTests
{
    private static EpisodeDecision Eval(Core.Domain.Series s, string title, SearchScope? scope = null, long sizeMb = 1500) =>
        EpisodeDecisionEngine.Evaluate(s, Hd1080, s.Episodes, Release(title, sizeMb: sizeMb), scope, requireMonitored: false, Today);

    private static AlternateTitle Title(string title, string? language = null, AlternateTitleSource source = AlternateTitleSource.Metadata) =>
        new() { Title = title, NormalizedTitle = SeriesService.NormalizeTitle(title), Language = language, Source = source };

    /// <summary>Scene numbering where TheTVDB's S02 continues scene season 1 (S02E01 = scene S01E06).</summary>
    private static Core.Domain.Series SceneMapped()
    {
        var s = Office();
        SceneNumbering.Apply(s.Episodes,
        [
            new SceneMapping(1, 1, 1, 1), new SceneMapping(1, 2, 1, 2), new SceneMapping(1, 3, 1, 3), new SceneMapping(1, 4, 1, 4),
            new SceneMapping(1, 5, 1, 5), new SceneMapping(2, 1, 1, 6), new SceneMapping(2, 2, 1, 7),
        ]);
        return s;
    }

    [Fact]
    public void Matches_releases_named_by_an_alternate_title()
    {
        var s = Office();
        Assert.Contains(Eval(s, "La.Oficina.S01E02.1080p.WEB-DL-GRP").Rejections, r => r.StartsWith("Series mismatch"));

        s.AlternateTitles.Add(Title("La Oficina", "es"));
        var d = Eval(s, "La.Oficina.S01E02.1080p.WEB-DL-GRP");
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
        Assert.Equal(2, Assert.Single(d.Episodes).EpisodeNumber);
    }

    [Fact]
    public void Alternate_titles_are_normalised_like_the_main_title()
    {
        var s = Office();
        s.AlternateTitles.Add(Title("Marvel's Agents of S.H.I.E.L.D. (2013)"));
        Assert.True(Eval(s, "Marvels.Agents.of.S.H.I.E.L.D.S01E02.1080p.WEB-DL-GRP").Approved);
    }

    [Fact]
    public void A_renamed_series_still_matches_its_metadata_title()
    {
        var s = Office();
        s.MetadataTitle = "The Office";
        s.Title = "The Office (UK fan cut)";
        Assert.True(Eval(s, "The.Office.S01E02.1080p.WEB-DL-GRP").Approved);
    }

    [Fact]
    public void Scene_numbered_episode_maps_to_the_library_episode()
    {
        var s = SceneMapped();
        var d = Eval(s, "The.Office.S01E06.1080p.WEB-DL-GRP");
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
        var ep = Assert.Single(d.Episodes);
        Assert.Equal((2, 1), (ep.SeasonNumber, ep.EpisodeNumber));

        // The library numbering of a remapped episode is not a scene number.
        Assert.Contains(Eval(s, "The.Office.S02E01.1080p.WEB-DL-GRP").Rejections, r => r.StartsWith("Unknown episode S02E01"));
    }

    [Fact]
    public void Scene_season_pack_covers_its_aired_episodes_across_library_seasons()
    {
        var d = Eval(SceneMapped(), "The.Office.S01.1080p.BluRay.x264-GRP", sizeMb: 9000);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
        Assert.Equal(["S01E01", "S01E02", "S01E03", "S01E04", "S02E01", "S02E02"],
            d.Episodes.Select(e => SeriesNaming.EpisodeCode(e.SeasonNumber, [e.EpisodeNumber])));
    }

    [Fact]
    public void Season_scope_uses_the_library_season_of_scene_numbered_episodes()
    {
        var s = SceneMapped();
        var episode = s.Episodes.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1);
        var ok = Eval(s, "The.Office.S01E06.1080p.WEB-DL-GRP", new SearchScope(2, new HashSet<int> { episode.Id }));
        Assert.True(ok.Approved, string.Join(", ", ok.Rejections));

        Assert.Contains(Eval(s, "The.Office.S01E02.1080p.WEB-DL-GRP", new SearchScope(2, null)).Rejections,
            r => r == "Wrong season (1 instead of 2)");
    }

    [Fact]
    public void Without_scene_mapping_numbers_are_the_library_ones()
    {
        var s = Office();
        Assert.Equal((2, 1), SceneNumbering.SceneNumbers(s.Episodes.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 1)));
        Assert.Equal(2, SceneNumbering.Find(s.Episodes, 2, 1)!.SeasonNumber);
        Assert.Equal(2, SceneNumbering.SearchSeason(s.Episodes.Where(e => e.SeasonNumber == 2), 2));
    }

    [Fact]
    public void Apply_clears_mappings_the_new_list_does_not_have()
    {
        var s = SceneMapped();
        Assert.Equal(7, SceneNumbering.Apply(s.Episodes, []));
        Assert.All(s.Episodes, e => Assert.Null(e.SceneSeasonNumber));
        Assert.Equal(0, SceneNumbering.Apply(s.Episodes, []));
    }

    [Fact]
    public void MapEpisodes_uses_scene_numbering()
    {
        var s = SceneMapped();
        var parsed = Media.Parsing.ReleaseParser.Parse("The.Office.S01E06E07.1080p.WEB-DL-GRP");
        Assert.Equal([(2, 1), (2, 2)], EpisodeDecisionEngine.MapEpisodes(s.Episodes, parsed, Today).Select(e => (e.SeasonNumber, e.EpisodeNumber)));
    }

    [Fact]
    public void Searches_use_scene_numbers_and_alternate_titles()
    {
        var s = SceneMapped();
        s.AlternateTitles.Add(Title("La Oficina (2005)", "es"));
        var episode = s.Episodes.Single(e => e.SeasonNumber == 2 && e.EpisodeNumber == 2);

        var single = SeriesService.Criteria(s, null, episode);
        Assert.Equal((1, 7), (single.Season, single.Episode));
        var alt = Assert.Single(single.AlternateTitles);
        Assert.Equal(new AlternateTitleInfo("La Oficina", "es"), alt);

        // All of library season 2 is scene season 1.
        Assert.Equal(1, SeriesService.Criteria(s, 2, null).Season);
        // Library season 1 maps to scene season 1 too.
        Assert.Equal(1, SeriesService.Criteria(s, 1, null).Season);
    }

    [Fact]
    public void Parses_xem_mappings_and_names()
    {
        using var map = JsonDocument.Parse("""
            {"result":"success","data":[
              {"scene":{"season":1,"episode":13,"absolute":13},"tvdb":{"season":2,"episode":1,"absolute":13}},
              {"scene":{"season":1,"episode":1},"tvdb":{"season":1}}
            ],"message":""}
            """);
        Assert.Equal([new SceneMapping(2, 1, 1, 13)], XemClient.ParseMappings(map.RootElement));

        using var names = JsonDocument.Parse("""
            {"result":"success","data":{"all":["Shingeki no Kyojin",{"Attack on Titan":"us"}],"2":["Shingeki no Kyojin Season 2"]}}
            """);
        Assert.Equal(["Shingeki no Kyojin", "Attack on Titan", "Shingeki no Kyojin Season 2"], XemClient.ParseNames(names.RootElement));
    }

    [Fact]
    public void Reads_tvdb_aliases_with_their_language()
    {
        using var doc = JsonDocument.Parse("""
            {"name":"Shingeki no Kyojin","originalLanguage":"jpn",
             "aliases":[{"language":"spa","name":"Ataque a los titanes"},{"language":"xyz","name":"Unknown lang"},"Bare name"]}
            """);
        Assert.Equal(
            [new AlternateTitleInfo("Shingeki no Kyojin", "ja"), new AlternateTitleInfo("Ataque a los titanes", "es"),
             new AlternateTitleInfo("Unknown lang"), new AlternateTitleInfo("Bare name")],
            TvdbClient.MapAlternateTitles(doc.RootElement));
    }
}
