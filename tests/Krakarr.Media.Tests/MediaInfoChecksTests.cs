using Krakarr.Core.Domain;
using Krakarr.Media.Files;

namespace Krakarr.Media.Tests;

public class MediaInfoChecksTests
{
    private static MediaInfoSummary Info(int width, int height, int? seconds = null, params AudioSummary[] audio) =>
        new() { Width = width, Height = height, DurationSeconds = seconds, VideoCodec = "HEVC", Audio = audio };

    [Fact]
    public void Corrects_the_resolution_and_keeps_the_source()
    {
        Assert.Equal(Qualities.WebDl720, MediaInfoChecks.Correct(Qualities.WebDl2160, Info(1280, 720)));
        Assert.Equal(Qualities.Bluray1080, MediaInfoChecks.Correct(Qualities.Bluray720, Info(1920, 800)));
        Assert.Equal(Qualities.WebDl2160, MediaInfoChecks.Correct(Qualities.Unknown, Info(3840, 1600)));
        Assert.Equal(Qualities.Hdtv1080, MediaInfoChecks.Correct(Qualities.Hdtv1080, Info(1440, 1080)));
    }

    [Fact]
    public void Keeps_qualities_the_resolution_says_nothing_about()
    {
        Assert.Equal(Qualities.Cam, MediaInfoChecks.Correct(Qualities.Cam, Info(1920, 1080)));
        Assert.Equal(Qualities.Dvd, MediaInfoChecks.Correct(Qualities.Dvd, Info(720, 576)));
        Assert.Equal(Qualities.WebDl1080, MediaInfoChecks.Correct(Qualities.WebDl1080, Info(0, 0)));
        Assert.Equal(Qualities.WebDl1080, MediaInfoChecks.Correct(Qualities.WebDl1080, null));
    }

    [Theory]
    [InlineData(60, 120, true)]   // a minute-long sample of a two-hour movie
    [InlineData(3000, 120, true)] // 50 min of 120
    [InlineData(5400, 120, false)]
    [InlineData(60, 5, false)]    // runtimes this short aren't judged
    [InlineData(60, 0, false)]    // runtime unknown
    public void Flags_files_far_shorter_than_the_runtime(int seconds, int runtime, bool flagged) =>
        Assert.Equal(flagged, MediaInfoChecks.TooShort(Info(1920, 1080, seconds), runtime) is not null);

    [Fact]
    public void Warns_about_mislabelled_files_and_unwanted_audio()
    {
        var info = Info(1280, 720, 7200, new AudioSummary("AAC", 2, "en"));

        var warnings = MediaInfoChecks.Warnings(info, 120, "Movie.2020.2160p.WEB-DL-GRP", ["es"]);

        Assert.Equal(2, warnings.Count);
        Assert.Contains("Named 2160p", warnings[0]);
        Assert.Contains("Audio is English; wanted: Spanish", warnings[1]);
        Assert.Empty(MediaInfoChecks.Warnings(info, 120, "Movie.2020.720p.WEB-DL-GRP", ["en", "es"]));
        Assert.Empty(MediaInfoChecks.Warnings(info, 120, "Movie.2020.WEB-DL-GRP", [])); // no resolution named, any language
    }

    [Fact]
    public void Untagged_audio_is_not_flagged() =>
        Assert.Null(MediaInfoChecks.UnwantedAudio(Info(1920, 1080, 7200, new AudioSummary("AAC", 2, null)), ["es"]));

    [Fact]
    public void Fills_media_info_naming_tokens()
    {
        var info = new MediaInfoSummary
        {
            VideoCodec = "HEVC", Width = 3840, Height = 2160, BitDepth = 10, DynamicRange = DynamicRange.DolbyVisionHdr10,
            Audio = [new("EAC3 Atmos", 6, "en"), new("AC3", 6, "es"), new("AAC", 2, "en")],
        };
        var tokens = new Dictionary<string, string?>();

        MediaInfoChecks.AddTokens(tokens, info);

        Assert.Equal("HEVC", tokens["MediaInfo VideoCodec"]);
        Assert.Equal("10", tokens["MediaInfo VideoBitDepth"]);
        Assert.Equal("DV HDR10", tokens["MediaInfo VideoDynamicRange"]);
        Assert.Equal("2160p", tokens["MediaInfo Resolution"]);
        Assert.Equal("EAC3 Atmos", tokens["MediaInfo AudioCodec"]);
        Assert.Equal("5.1", tokens["MediaInfo AudioChannels"]);
        Assert.Equal("EN+ES", tokens["MediaInfo AudioLanguages"]);
        Assert.Equal("HEVC EAC3 Atmos", tokens["MediaInfo Simple"]);
        Assert.Equal("Movie [HEVC DV HDR10][EN+ES]",
            FileNameBuilder.Build("Movie [{MediaInfo VideoCodec} {MediaInfo VideoDynamicRange}][{MediaInfo AudioLanguages}]", tokens));
    }

    [Fact]
    public void Missing_media_info_leaves_tokens_empty()
    {
        var tokens = new Dictionary<string, string?>();
        MediaInfoChecks.AddTokens(tokens, null);
        Assert.Equal("Movie", FileNameBuilder.Build("Movie [{MediaInfo VideoCodec}] {MediaInfo AudioLanguages}", tokens));
    }

    [Theory]
    [InlineData("spa", "es")]
    [InlineData("ger", "de")]
    [InlineData("deu", "de")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("es-ES", "es")]
    [InlineData("dan", "da")]
    [InlineData("und", null)]
    [InlineData("tgl", "tgl")]
    public void Normalizes_track_languages(string tag, string? expected) => Assert.Equal(expected, MediaInfoSummary.TrackLanguage(tag));

    [Fact]
    public void Stored_form_stays_within_the_column()
    {
        var info = Info(1920, 1080, 100, Enumerable.Range(0, 400).Select(_ => new AudioSummary("DTS-HD MA", 8, "abcdefghijkl")).ToArray());

        var stored = MediaInfoSummary.Serialize(info);

        Assert.InRange(stored.Length, 1, MediaInfoSummary.MaxLength);
        Assert.Equal(1920, MediaInfoSummary.Parse(stored)!.Width);
    }
}
