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
}
