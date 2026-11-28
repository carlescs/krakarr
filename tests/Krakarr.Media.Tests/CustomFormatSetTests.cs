using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;
using Krakarr.Media.Parsing;

namespace Krakarr.Media.Tests;

public class CustomFormatSetTests
{
    private static CustomFormatSpecification Spec(CustomFormatCondition condition, string value, bool negate = false, bool required = false) =>
        new() { Name = value, Condition = condition, Value = value, Negate = negate, Required = required };

    private static CustomFormat Format(int id, string name, params CustomFormatSpecification[] specs) => new() { Id = id, Name = name, Specifications = [.. specs] };

    private static IReadOnlyList<string> Match(CustomFormatSet set, string title, long size = 0, string? indexer = null)
    {
        var parsed = ReleaseParser.Parse(title);
        return set.Match(new CustomFormatInput { Parsed = parsed, Languages = parsed.Languages, Size = size, IndexerName = indexer }).Select(m => m.Name).ToList();
    }

    [Fact]
    public void Matches_each_condition_kind()
    {
        var set = CustomFormatSet.Compile(
        [
            Format(1, "Title", Spec(CustomFormatCondition.ReleaseTitle, @"\bREPACK\b")),
            Format(2, "Group", Spec(CustomFormatCondition.ReleaseGroup, "^FLUX$")),
            Format(3, "HDR", Spec(CustomFormatCondition.Hdr, "HDR")),
            Format(4, "DV-only", Spec(CustomFormatCondition.Hdr, CustomFormatSet.DolbyVisionOnly)),
            Format(5, "x265", Spec(CustomFormatCondition.VideoCodec, "x265")),
            Format(6, "Atmos", Spec(CustomFormatCondition.Audio, "Atmos")),
            Format(7, "5.1", Spec(CustomFormatCondition.Audio, "5.1")),
            Format(8, "IMAX", Spec(CustomFormatCondition.Edition, "imax")),
            Format(9, "AMZN", Spec(CustomFormatCondition.StreamingService, "AMZN")),
            Format(10, "WEB", Spec(CustomFormatCondition.Source, "WebDl")),
            Format(11, "2160p", Spec(CustomFormatCondition.Resolution, "2160")),
            Format(12, "French", Spec(CustomFormatCondition.Language, "fr")),
            Format(13, "Pack", Spec(CustomFormatCondition.ReleaseType, CustomFormatSet.SeasonPack)),
            Format(14, "3D", Spec(CustomFormatCondition.Flag, ReleaseTags.Flag3D)),
            Format(15, "Indexer", Spec(CustomFormatCondition.Indexer, "^Private")),
        ]);

        Assert.Equal(["HDR", "DV-only", "x265", "Atmos", "5.1", "IMAX", "AMZN", "WEB", "2160p"],
            Match(set, "Movie.2020.IMAX.2160p.AMZN.WEB-DL.DDP5.1.Atmos.DV.H.265-GRP"));
        Assert.Equal(["Title", "Group"], Match(set, "Movie.2020.REPACK.1080p.BluRay.x264-FLUX"));
        Assert.Equal(["French", "Pack", "Indexer"], Match(set, "Show.S01.FRENCH.1080p.BluRay.x264-GRP", indexer: "Private Tracker"));
        Assert.Equal(["3D"], Match(set, "Avatar.2009.3D.1080p.BluRay.Half-SBS.x264-GRP"));
    }

    [Fact]
    public void Bluray_source_includes_remuxes_and_modifier_narrows_to_them()
    {
        var set = CustomFormatSet.Compile(
        [
            Format(1, "Bluray", Spec(CustomFormatCondition.Source, "Bluray")),
            Format(2, "Remux", Spec(CustomFormatCondition.QualityModifier, "Remux")),
        ]);
        Assert.Equal(["Bluray", "Remux"], Match(set, "Movie.2020.2160p.BluRay.REMUX.HEVC-FGT"));
        Assert.Equal(["Bluray"], Match(set, "Movie.2020.1080p.BluRay.x264-GRP"));
    }

    [Fact]
    public void Needs_one_condition_per_kind_and_every_required_one()
    {
        // TRaSH "x265 (HD)": x265 required, and not 2160p.
        var set = CustomFormatSet.Compile(
        [
            Format(1, "x265 (HD)",
                Spec(CustomFormatCondition.ReleaseTitle, "[xh][ ._-]?265|\\bHEVC(\\b|\\d)", required: true),
                Spec(CustomFormatCondition.Resolution, "2160", negate: true, required: true)),
            // Either streaming service is enough.
            Format(2, "AMZN or NF", Spec(CustomFormatCondition.StreamingService, "AMZN"), Spec(CustomFormatCondition.StreamingService, "NF")),
            // One of each kind: an AMZN or NF release that is also 2160p.
            Format(3, "UHD stream",
                Spec(CustomFormatCondition.StreamingService, "AMZN"), Spec(CustomFormatCondition.StreamingService, "NF"),
                Spec(CustomFormatCondition.Resolution, "2160")),
        ]);

        Assert.Equal(["x265 (HD)", "AMZN or NF"], Match(set, "Movie.2020.1080p.NF.WEB-DL.DDP5.1.x265-GRP"));
        Assert.Equal(["AMZN or NF", "UHD stream"], Match(set, "Movie.2020.2160p.AMZN.WEB-DL.DDP5.1.H.265-GRP"));
        Assert.Empty(Match(set, "Movie.2020.1080p.BluRay.x264-GRP"));
    }

    [Fact]
    public void Size_condition_uses_gigabytes_and_ignores_unknown_sizes()
    {
        var set = CustomFormatSet.Compile([Format(1, "Big", new CustomFormatSpecification { Condition = CustomFormatCondition.Size, Min = 20, Max = 0 })]);
        Assert.Equal(["Big"], Match(set, "Movie.2020.2160p.BluRay.x265-GRP", size: 30L * 1024 * 1024 * 1024));
        Assert.Empty(Match(set, "Movie.2020.2160p.BluRay.x265-GRP", size: 10L * 1024 * 1024 * 1024));
        Assert.Empty(Match(set, "Movie.2020.2160p.BluRay.x265-GRP", size: 0));
    }

    [Fact]
    public void Invalid_regexes_never_match_and_fail_validation()
    {
        var bad = Spec(CustomFormatCondition.ReleaseTitle, "([unclosed");
        Assert.NotNull(CustomFormatSet.Validate(bad));
        Assert.Empty(Match(CustomFormatSet.Compile([Format(1, "Bad", bad)]), "Movie.2020.1080p.BluRay.x264-GRP"));
        Assert.NotNull(CustomFormatSet.Validate(Spec(CustomFormatCondition.Hdr, "HDR11")));
        Assert.Null(CustomFormatSet.Validate(Spec(CustomFormatCondition.Hdr, "hdr10+")));
    }

    [Fact]
    public void Library_files_use_stored_formats_or_fall_back_to_their_scene_name()
    {
        var set = CustomFormatSet.Compile([Format(1, "Atmos", Spec(CustomFormatCondition.Audio, "Atmos")), Format(2, "x265", Spec(CustomFormatCondition.VideoCodec, "x265"))]);
        var profile = new QualityProfile { Name = "P", FormatScores = [new(1, 100), new(2, 10)] };

        Assert.Equal(110, set.ScoreOf(new MovieFile { RelativePath = "a.mkv", SceneName = "Movie.2020.2160p.BluRay.TrueHD.Atmos.x265-GRP" }, profile));
        Assert.Equal(10, set.ScoreOf(new MovieFile { RelativePath = "a.mkv", SceneName = "Movie.2020.2160p.BluRay.TrueHD.Atmos.x265-GRP", CustomFormatIds = [2] }, profile));
        Assert.Equal(0, set.ScoreOf(new MovieFile { RelativePath = "a.mkv" }, profile));
    }

    [Fact]
    public void Import_records_the_matched_formats_and_score()
    {
        var set = CustomFormatSet.Compile([Format(4, "Atmos", Spec(CustomFormatCondition.Audio, "Atmos"))]);
        var file = new EpisodeFile { RelativePath = "a.mkv" };
        LibraryFileFormats.Apply(file, set, new QualityProfile { Name = "P", FormatScores = [new(4, 50)] }, "Show.S01E01.2160p.WEB-DL.DDP5.1.Atmos.H.265-GRP", "Indexer");
        Assert.Equal([4], file.CustomFormatIds);
        Assert.Equal(50, file.CustomFormatScore);
    }
}
