using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;

namespace Krakarr.Movies.Tests;

public class MovieCustomFormatTests
{
    private static readonly Movie Matrix = new() { Id = 1, TmdbId = 603, Title = "The Matrix", Year = 1999, Path = "/m" };

    private static readonly CustomFormatSet Formats = CustomFormatSet.Compile(
    [
        new CustomFormat { Id = 1, Name = "Atmos", Specifications = [new() { Condition = CustomFormatCondition.Audio, Value = "Atmos" }] },
        new CustomFormat { Id = 2, Name = "x265", Specifications = [new() { Condition = CustomFormatCondition.VideoCodec, Value = "x265" }] },
        new CustomFormat { Id = 3, Name = "Hardcoded", Specifications = [new() { Condition = CustomFormatCondition.Flag, Value = "Hardcoded subs" }] },
    ]);

    private static QualityProfile Profile(int min = 0, int cutoff = 0) => new()
    {
        Name = "HD",
        AllowedQualityIds = [Qualities.WebDl1080.Id, Qualities.Bluray1080.Id],
        CutoffQualityId = Qualities.Bluray1080.Id,
        FormatScores = [new(1, 100), new(2, 20), new(3, -1000)],
        MinFormatScore = min,
        CutoffFormatScore = cutoff,
    };

    private static ReleaseInfo Release(string title, int seeders = 10) => new() { Title = title, Guid = title, Size = 8L << 30, Seeders = seeders };

    private static ReleaseDecision Eval(QualityProfile profile, string title, Quality? existing = null, int existingScore = 0) =>
        MovieDecisionEngine.Evaluate(Matrix, profile, existing, Release(title), formats: Formats, existingFormatScore: existingScore);

    [Fact]
    public void Scores_matched_formats()
    {
        var d = Eval(Profile(), "The.Matrix.1999.1080p.BluRay.TrueHD.7.1.Atmos.x265-GRP");
        Assert.Equal(["Atmos", "x265"], d.CustomFormats.Select(f => f.Name));
        Assert.Equal(120, d.CustomFormatScore);
        Assert.True(d.Approved);
    }

    [Fact]
    public void Rejects_releases_below_the_minimum_score()
    {
        var hardcoded = Eval(Profile(), "The.Matrix.1999.1080p.WEB-DL.HC.x264-GRP");
        Assert.Contains(hardcoded.Rejections, r => r.StartsWith("Custom format score -1000 is below the minimum (0)"));

        var strict = Profile(min: 50);
        Assert.False(Eval(strict, "The.Matrix.1999.1080p.BluRay.x265-GRP").Approved);
        Assert.True(Eval(strict, "The.Matrix.1999.1080p.BluRay.Atmos.x264-GRP").Approved);
    }

    [Fact]
    public void Ranks_by_quality_then_score()
    {
        var ranked = MovieDecisionEngine.Evaluate(Matrix, Profile(), null,
        [
            Release("The.Matrix.1999.1080p.BluRay.x264-PLAIN", seeders: 500),
            Release("The.Matrix.1999.1080p.WEB-DL.DDP5.1.Atmos.x265-STREAM", seeders: 500),
            Release("The.Matrix.1999.1080p.BluRay.Atmos.x264-ATMOS", seeders: 1),
            Release("The.Matrix.1999.1080p.BluRay.x265-TINY", seeders: 1),
        ], formats: Formats);

        Assert.Equal(["ATMOS", "TINY", "PLAIN", "STREAM"], ranked.Select(d => d.Parsed.ReleaseGroup));
    }

    [Fact]
    public void Same_quality_with_a_better_score_is_an_upgrade_until_the_cutoff_score()
    {
        var profile = Profile(cutoff: 100);

        // Quality cutoff met, score below "upgrade until": a better score upgrades, an equal one does not.
        Assert.True(Eval(profile, "The.Matrix.1999.1080p.BluRay.Atmos.x264-GRP", Qualities.Bluray1080, existingScore: 20).Approved);
        Assert.Contains(Eval(profile, "The.Matrix.1999.1080p.BluRay.x265-GRP", Qualities.Bluray1080, existingScore: 20).Rejections,
            r => r.StartsWith("Not an upgrade over existing Bluray-1080p, score 20"));

        // Score cutoff reached too: nothing more to grab.
        Assert.Contains(Eval(profile, "The.Matrix.1999.1080p.BluRay.Atmos.x265-GRP", Qualities.Bluray1080, existingScore: 100).Rejections,
            r => r.StartsWith("Existing file meets cutoff"));

        // A worse quality is never an upgrade, whatever its score.
        Assert.Contains(Eval(profile, "The.Matrix.1999.1080p.WEB-DL.Atmos.x265-GRP", Qualities.Bluray1080, existingScore: 0).Rejections,
            r => r.StartsWith("Not an upgrade"));
    }

    [Fact]
    public void Without_an_upgrade_until_score_only_quality_upgrades()
    {
        var profile = Profile();
        Assert.Contains(Eval(profile, "The.Matrix.1999.1080p.BluRay.Atmos.x265-GRP", Qualities.Bluray1080).Rejections, r => r.StartsWith("Existing file meets cutoff"));
        Assert.Contains(Eval(profile, "The.Matrix.1999.1080p.WEB-DL.Atmos.x265-GRP", Qualities.WebDl1080).Rejections, r => r.StartsWith("Not an upgrade"));
        Assert.True(Eval(profile, "The.Matrix.1999.1080p.BluRay.x264-GRP", Qualities.WebDl1080, existingScore: 120).Approved);
    }
}
