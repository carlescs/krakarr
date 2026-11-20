using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class ReleaseLanguagesTests
{
    private static ReleaseInfo Release(string title, string? indexerLanguage = null, IReadOnlyList<string>? languages = null) => new()
    {
        Title = title,
        Guid = title,
        IndexerLanguage = indexerLanguage,
        Languages = languages ?? [],
    };

    private static IReadOnlyList<string> Resolve(string title, string? indexerLanguage = null) =>
        ReleaseLanguages.Resolve(ReleaseParser.Parse(title), Release(title, indexerLanguage));

    [Theory]
    [InlineData("Amelie.2001.FRENCH.1080p.BluRay.x264-GRP", "fr")]
    [InlineData("Amelie.2001.TRUEFRENCH.1080p.BluRay.x264-GRP", "fr")]
    [InlineData("Amelie 2001 VFF 1080p WEB-DL", "fr")]
    [InlineData("Ocho.Apellidos.Vascos.2014.SPANISH.1080p.BluRay.x264-GRP", "es")]
    [InlineData("Ocho Apellidos Vascos (2014) [BluRay 1080p][Castellano]", "es")]
    [InlineData("Movie.2020.LATINO.1080p.WEB-DL", "es-419")]
    [InlineData("Das.Boot.1981.GERMAN.DL.1080p.BluRay.x264-GRP", "de")]
    [InlineData("Movie.2020.iTA.1080p.WEB-DL.x264-GRP", "it")]
    [InlineData("Movie.2020.PT-BR.1080p.WEB-DL", "pt-br")]
    [InlineData("Show.S01E01.FRENCH.720p.HDTV.x264-GRP", "fr")]
    public void Detects_tagged_language(string title, string code) =>
        Assert.Equal([code], ReleaseParser.Parse(title).Languages);

    [Theory]
    [InlineData("French.Kiss.1995.1080p.BluRay.x264-GRP")]
    [InlineData("The.Italian.Job.2003.1080p.BluRay.x264-GRP")]
    [InlineData("The.French.Dispatch.2021.1080p.WEB-DL.x264-GRP")]
    [InlineData("Movie.2020.1080p.WEB-DL.ENG.SUBS.x264-GRP")]
    [InlineData("Movie.2020.1080p.WEB-DL.SUB.ITA.x264-GRP")]
    [InlineData("Movie.2020.VOSTFR.1080p.WEB-DL.x264-GRP")]
    public void Ignores_titles_and_subtitle_tags(string title) =>
        Assert.Empty(ReleaseParser.Parse(title).Languages);

    [Fact]
    public void Untagged_release_is_english_unless_the_indexer_says_otherwise()
    {
        Assert.Equal(["en"], Resolve("The.Matrix.1999.1080p.BluRay.x264-GRP"));
        Assert.Equal(["es"], Resolve("The.Matrix.1999.1080p.BluRay.x264-GRP", indexerLanguage: "es"));
        // A tag in the name beats the indexer default.
        Assert.Equal(["fr"], Resolve("The.Matrix.1999.FRENCH.1080p.BluRay.x264-GRP", indexerLanguage: "es"));
    }

    [Fact]
    public void Multi_releases_include_english_and_the_indexer_language()
    {
        Assert.Equal(["fr", "en"], Resolve("Movie.2020.MULTi.FRENCH.1080p.BluRay.x264-GRP"));
        Assert.Equal(["fr", "en"], Resolve("Movie.2020.MULTi.1080p.BluRay.x264-GRP", indexerLanguage: "fr"));
        Assert.Equal(["en"], Resolve("Movie.2020.1080p.WEB-DL.MULTi.SUBS-GRP"));
    }

    [Fact]
    public void Indexer_reported_languages_count()
    {
        var title = "The.Matrix.1999.1080p.BluRay.x264-GRP";
        Assert.Equal(["de"], ReleaseLanguages.Resolve(ReleaseParser.Parse(title), Release(title, languages: ["de"])));
    }

    [Fact]
    public void Preference_rank_follows_wanted_order()
    {
        Assert.Equal(0, ReleaseLanguages.PreferenceRank(["fr"], []));
        Assert.Equal(0, ReleaseLanguages.PreferenceRank(["fr"], null));
        Assert.Equal(1, ReleaseLanguages.PreferenceRank(["en", "fr"], ["es", "en"]));
        Assert.Null(ReleaseLanguages.PreferenceRank(["fr"], ["es", "en"]));
    }

    [Fact]
    public void Clean_normalizes_and_drops_unknown_codes() =>
        Assert.Equal(["en", "pt-br", "fr"], MediaLanguages.Clean(["EN", "pt_BR", "klingon", "French", "en"]));
}
