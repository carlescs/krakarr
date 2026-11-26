using Krakarr.Core.Domain;

namespace Krakarr.Movies.Tests;

public class MovieDecisionEngineTests
{
    private static readonly Movie Matrix = new() { Id = 1, TmdbId = 603, ImdbId = "tt0133093", Title = "The Matrix", Year = 1999, Path = "/media/movies/The Matrix (1999)" };

    private static readonly QualityProfile Hd1080 = new()
    {
        Id = 1,
        Name = "HD-1080p",
        AllowedQualityIds = [Qualities.WebDl1080.Id, Qualities.WebRip1080.Id, Qualities.Bluray1080.Id],
        CutoffQualityId = Qualities.Bluray1080.Id,
    };

    private static ReleaseInfo Release(string title, int? seeders = 10, long sizeGb = 8, int priority = 25) => new()
    {
        Title = title,
        Guid = title,
        DownloadUrl = "http://x/" + title,
        Size = sizeGb * 1024 * 1024 * 1024,
        Seeders = seeders,
        IndexerPriority = priority,
    };

    [Fact]
    public void Accepts_matching_release()
    {
        var d = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, Release("The.Matrix.1999.1080p.BluRay.x264-SPARKS"));
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
    }

    [Theory]
    [InlineData("The.Matrix.Reloaded.2003.1080p.BluRay.x264-GRP", "Title mismatch")]
    [InlineData("The.Matrix.2021.1080p.BluRay.x264-GRP", "Year mismatch")]
    [InlineData("The.Matrix.1999.720p.BluRay.x264-GRP", "not wanted")]
    [InlineData("The.Matrix.1999.x264-GRP", "Unknown quality")]
    [InlineData("The.Matrix.S01E01.1080p.WEB-DL-GRP", "TV episode")]
    public void Rejects_wrong_releases(string title, string reason)
    {
        var d = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, Release(title));
        Assert.False(d.Approved);
        Assert.Contains(d.Rejections, r => r.Contains(reason, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Accepts_title_mismatch_when_ids_match()
    {
        var release = Release("Matrix.Original.Cut.1999.1080p.BluRay-GRP") with { ImdbId = 133093 };
        Assert.True(MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, release).Approved);
    }

    [Fact]
    public void Rejects_releases_without_seeders()
    {
        var d = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, Release("The.Matrix.1999.1080p.BluRay-GRP", seeders: 0));
        Assert.Contains("No seeders", d.Rejections);
    }

    [Fact]
    public void Enforces_size_limits()
    {
        var profile = new QualityProfile
        {
            Name = "Sized",
            AllowedQualityIds = Hd1080.AllowedQualityIds,
            CutoffQualityId = Hd1080.CutoffQualityId,
            MinSizeMb = 2000,
            MaxSizeMb = 10000,
        };
        Assert.False(MovieDecisionEngine.Evaluate(Matrix, profile, null, Release("The.Matrix.1999.1080p.BluRay-GRP", sizeGb: 1)).Approved);
        Assert.False(MovieDecisionEngine.Evaluate(Matrix, profile, null, Release("The.Matrix.1999.1080p.BluRay-GRP", sizeGb: 20)).Approved);
        Assert.True(MovieDecisionEngine.Evaluate(Matrix, profile, null, Release("The.Matrix.1999.1080p.BluRay-GRP", sizeGb: 5)).Approved);
    }

    [Fact]
    public void Only_accepts_upgrades_below_cutoff()
    {
        var webdl = MovieDecisionEngine.Evaluate(Matrix, Hd1080, Qualities.WebDl1080, Release("The.Matrix.1999.1080p.WEB-DL-GRP"));
        Assert.Contains(webdl.Rejections, r => r.StartsWith("Not an upgrade"));

        var bluray = MovieDecisionEngine.Evaluate(Matrix, Hd1080, Qualities.WebDl1080, Release("The.Matrix.1999.1080p.BluRay-GRP"));
        Assert.True(bluray.Approved);

        var cutoffMet = MovieDecisionEngine.Evaluate(Matrix, Hd1080, Qualities.Bluray1080, Release("The.Matrix.1999.1080p.BluRay-GRP"));
        Assert.Contains(cutoffMet.Rejections, r => r.StartsWith("Existing file meets cutoff"));
    }

    [Fact]
    public void Ranks_best_quality_then_seeders()
    {
        var ranked = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null,
        [
            Release("The.Matrix.1999.1080p.WEB-DL-A", seeders: 100),
            Release("The.Matrix.1999.1080p.BluRay-B", seeders: 5),
            Release("The.Matrix.1999.1080p.BluRay-C", seeders: 50),
            Release("The.Matrix.1999.720p.BluRay-D", seeders: 500),
        ]);

        Assert.Equal(["The.Matrix.1999.1080p.BluRay-C", "The.Matrix.1999.1080p.BluRay-B", "The.Matrix.1999.1080p.WEB-DL-A", "The.Matrix.1999.720p.BluRay-D"],
            ranked.Select(r => r.Release.Title));
        Assert.False(ranked[^1].Approved);
    }

    [Fact]
    public void Builds_names_from_templates()
    {
        Assert.Equal("The Matrix (1999)", MovieNaming.BuildFolderName(Matrix, "{Movie Title} ({Release Year})"));
        Assert.Equal("The Matrix (1999) [Bluray-1080p].mkv",
            MovieNaming.BuildFileName(Matrix, "{Movie Title} ({Release Year}) [{Quality}]", Qualities.Bluray1080, "SPARKS", ".MKV"));
    }

    [Fact]
    public void Rejects_releases_in_unwanted_languages()
    {
        var french = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, Release("The.Matrix.1999.FRENCH.1080p.BluRay.x264-GRP"), ["en"]);
        Assert.False(french.Approved);
        Assert.Contains(french.Rejections, r => r.StartsWith("Language not wanted"));

        // Any language when none are configured.
        Assert.True(MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, Release("The.Matrix.1999.FRENCH.1080p.BluRay.x264-GRP"), []).Approved);
    }

    [Fact]
    public void Indexer_default_language_applies_to_untagged_releases()
    {
        var release = Release("The.Matrix.1999.1080p.BluRay.x264-GRP") with { IndexerLanguage = "es" };
        Assert.True(MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, release, ["es"]).Approved);
        Assert.False(MovieDecisionEngine.Evaluate(Matrix, Hd1080, null, release, ["en"]).Approved);
    }

    [Fact]
    public void Preferred_language_ranks_above_quality()
    {
        var decisions = MovieDecisionEngine.Evaluate(Matrix, Hd1080, null,
            [Release("The.Matrix.1999.1080p.BluRay.x264-GRP"), Release("The.Matrix.1999.SPANISH.1080p.WEB-DL.x264-GRP")],
            ["es", "en"]);
        Assert.All(decisions, d => Assert.True(d.Approved));
        Assert.Equal(["es"], decisions[0].Languages);
    }
}

public class MovieAlternateTitleTests
{
    private static readonly QualityProfile Hd1080 = new()
    {
        Id = 1, Name = "HD-1080p", AllowedQualityIds = [Qualities.Bluray1080.Id], CutoffQualityId = Qualities.Bluray1080.Id,
    };

    private static ReleaseInfo Release(string title) => new()
    {
        Title = title, Guid = title, DownloadUrl = "http://x/" + title, Size = 8L * 1024 * 1024 * 1024, Seeders = 10,
    };

    private static Movie Spirited() => new()
    {
        Id = 2, TmdbId = 129, Title = "Spirited Away", OriginalTitle = "千と千尋の神隠し", Year = 2001, Path = "/m/Spirited Away (2001)",
    };

    [Fact]
    public void Accepts_releases_named_by_an_alternate_title()
    {
        var movie = Spirited();
        var release = Release("El.Viaje.de.Chihiro.2001.1080p.BluRay.x264-GRP");
        Assert.Contains(MovieDecisionEngine.Evaluate(movie, Hd1080, null, release).Rejections, r => r.StartsWith("Title mismatch"));

        movie.AlternateTitles.Add(new AlternateTitle { Title = "El viaje de Chihiro", NormalizedTitle = "elviajedechihiro", Language = "es" });
        movie.AlternateTitles.Add(new AlternateTitle { Title = "Sen to Chihiro no Kamikakushi", NormalizedTitle = "sentochihironokamikakushi" });
        var d = MovieDecisionEngine.Evaluate(movie, Hd1080, null, release);
        Assert.True(d.Approved, string.Join(", ", d.Rejections));
        Assert.True(MovieDecisionEngine.Evaluate(movie, Hd1080, null, Release("Sen.to.Chihiro.no.Kamikakushi.2001.1080p.BluRay.x264-GRP")).Approved);

        // The year still has to fit.
        Assert.Contains(MovieDecisionEngine.Evaluate(movie, Hd1080, null, Release("El.Viaje.de.Chihiro.2015.1080p.BluRay.x264-GRP")).Rejections,
            r => r.StartsWith("Year mismatch"));
    }

    [Fact]
    public void Search_criteria_carry_alternate_titles_with_the_year()
    {
        var movie = Spirited();
        movie.AlternateTitles.Add(new AlternateTitle { Title = "El viaje de Chihiro", NormalizedTitle = "elviajedechihiro", Language = "es" });
        var criteria = MovieService.Criteria(movie);
        Assert.Equal("Spirited Away 2001", criteria.Query);
        Assert.Equal([new AlternateTitleInfo("El viaje de Chihiro 2001", "es")], criteria.AlternateTitles);
    }

    [Fact]
    public void Reads_tmdb_original_translated_and_alternative_titles()
    {
        var json = """
            {"id":129,"title":"Spirited Away","original_title":"千と千尋の神隠し","original_language":"ja",
             "translations":{"translations":[
               {"iso_3166_1":"ES","iso_639_1":"es","data":{"title":"El viaje de Chihiro"}},
               {"iso_3166_1":"MX","iso_639_1":"es","data":{"title":"El viaje de Chihiro (MX)"}},
               {"iso_3166_1":"US","iso_639_1":"en","data":{"title":""}}]},
             "alternative_titles":{"titles":[{"iso_3166_1":"JP","title":"Sen to Chihiro no Kamikakushi"},{"iso_3166_1":"FR","title":"Le Voyage de Chihiro"}]}}
            """;
        var movie = System.Text.Json.JsonSerializer.Deserialize<TmdbClient.TmdbMovie>(json)!;
        Assert.Equal(
            [
                new AlternateTitleInfo("千と千尋の神隠し", "ja"), new AlternateTitleInfo("El viaje de Chihiro", "es"),
                new AlternateTitleInfo("El viaje de Chihiro (MX)", "es-419"), new AlternateTitleInfo("Sen to Chihiro no Kamikakushi", "ja"),
                new AlternateTitleInfo("Le Voyage de Chihiro", "fr"),
            ],
            TmdbClient.MapAlternateTitles(movie));
        Assert.Equal("ja", TmdbClient.Map(movie).OriginalLanguage);
    }
}
