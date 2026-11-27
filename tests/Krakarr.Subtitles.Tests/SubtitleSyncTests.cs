using System.Text;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Subtitles.Sync;

namespace Krakarr.Subtitles.Tests;

public sealed class SubtitleUpgradeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly SubtitleSettings Settings = new() { Languages = ["en", "es", "fr"], UpgradeDays = 7, UpgradeBelowScore = 90 };

    private static SubtitleFile Row(string language, int? score, string provider = "opensubtitles", int ageDays = 1, bool manual = false, bool forced = false) => new()
    {
        Language = language, Score = score, Provider = provider, DateAdded = Now.AddDays(-ageDays), Manual = manual, Forced = forced, Path = $"/x.{language}.srt",
    };

    [Fact]
    public void Picks_recent_automatic_subtitles_below_the_upgrade_score()
    {
        var picked = SubtitleUpgrade.Select([Row("en", 75), Row("es", 95), Row("fr", 89, ageDays: 6)], Settings, Now, all: false);
        Assert.Equal(new Dictionary<string, int> { ["en"] = 75, ["fr"] = 89 }, picked);
    }

    [Fact]
    public void Skips_old_manual_disk_forced_and_unwanted_subtitles()
    {
        SubtitleFile[] rows =
        [
            Row("en", 60, ageDays: 8),
            Row("es", 60, manual: true),
            Row("fr", null, provider: "disk"),
            Row("de", 50),
            Row("fr", 40, forced: true),
        ];
        Assert.Empty(SubtitleUpgrade.Select(rows, Settings, Now, all: false));
    }

    [Fact]
    public void Upgrades_are_off_with_zero_days()
    {
        Assert.Empty(SubtitleUpgrade.Select([Row("en", 10)], Settings with { UpgradeDays = 0 }, Now, all: false));
    }

    [Fact]
    public void A_search_by_hand_takes_every_downloaded_subtitle_but_not_disk_ones()
    {
        SubtitleFile[] rows = [Row("en", 95, ageDays: 30, manual: true), Row("es", null, provider: "disk"), Row("fr", 70)];
        var picked = SubtitleUpgrade.Select(rows, Settings with { UpgradeDays = 0 }, Now, all: true);
        Assert.Equal(new Dictionary<string, int> { ["en"] = 95, ["fr"] = 70 }, picked);
    }
}

public sealed class SubtitleTimingTests
{
    private static string Transform(string text, double offsetMs, double ratio = 1, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        using var input = new MemoryStream(encoding.GetBytes(text));
        using var output = new MemoryStream();
        SubtitleTiming.Transform(input, output, offsetMs, ratio);
        return encoding.GetString(output.ToArray());
    }

    [Fact]
    public void Shifts_subrip_and_keeps_everything_else()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:03,500 X1:10\r\nHéllo — wörld\r\n\r\n2\r\n00:59:59,900 --> 01:00:00,400\r\nBye\r\n";
        var shifted = Transform(srt, 250);
        Assert.Equal("1\r\n00:00:01,250 --> 00:00:03,750 X1:10\r\nHéllo — wörld\r\n\r\n2\r\n01:00:00,150 --> 01:00:00,650\r\nBye\r\n", shifted);
    }

    [Fact]
    public void Negative_shifts_stop_at_zero()
    {
        var shifted = Transform("1\n00:00:00,500 --> 00:00:02,000\nHi", -1000);
        Assert.Equal("1\n00:00:00,000 --> 00:00:01,000\nHi", shifted);
    }

    [Fact]
    public void Shifts_webvtt_with_and_without_hours()
    {
        const string vtt = "WEBVTT\n\n00:01.000 --> 00:04.000 align:start\nOne\n\n00:59:58.000 --> 01:00:02.000\nTwo\n";
        Assert.Equal("WEBVTT\n\n00:03.500 --> 00:06.500 align:start\nOne\n\n01:00:00.500 --> 01:00:04.500\nTwo\n", Transform(vtt, 2500));
        // Past an hour, a time without hours gains them.
        Assert.Contains("01:00:01.000 --> 01:00:04.000", Transform(vtt, 3_600_000));
    }

    [Fact]
    public void Shifts_substation_alpha_dialogue_lines()
    {
        const string ass = "[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:01.50,0:00:04.00,Default,,0,0,0,,Hello, world\n";
        Assert.Equal("[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.50,0:00:03.00,Default,,0,0,0,,Hello, world\n", Transform(ass, -1000));
    }

    [Fact]
    public void Applies_a_framerate_ratio_before_the_offset()
    {
        var shifted = Transform("1\n00:00:10,000 --> 00:00:20,000\nHi\n", 1000, 1.5);
        Assert.Equal("1\n00:00:16,000 --> 00:00:31,000\nHi\n", shifted);
    }

    [Fact]
    public void Keeps_legacy_code_pages_and_utf16_intact()
    {
        var latin1 = Encoding.Latin1;
        var original = latin1.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nCaça à l'été\n");
        using (var input = new MemoryStream(original))
        using (var output = new MemoryStream())
        {
            SubtitleTiming.Transform(input, output, 1000);
            Assert.Equal(latin1.GetBytes("1\n00:00:02,000 --> 00:00:03,000\nCaça à l'été\n"), output.ToArray());
        }

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("1\r\n00:00:01,000 --> 00:00:02,000\r\nHi\r\n")).ToArray();
        using (var input = new MemoryStream(utf16))
        using (var output = new MemoryStream())
        {
            SubtitleTiming.Transform(input, output, 500);
            var expected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("1\r\n00:00:01,500 --> 00:00:02,500\r\nHi\r\n")).ToArray();
            Assert.Equal(expected, output.ToArray());
        }
    }

    [Fact]
    public void Reads_cues()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("1\n00:00:01,5 --> 00:00:02,25\nA\n\n2\n00:01:00,000 --> 00:01:01,000\nB\n"));
        Assert.Equal([new Cue(1500, 2250), new Cue(60_000, 61_000)], SubtitleTiming.ReadCues(stream));
    }

    [Fact]
    public void Retimes_files_in_place()
    {
        var dir = Directory.CreateTempSubdirectory("krakarr-timing");
        try
        {
            var path = Path.Combine(dir.FullName, "movie.en.srt");
            File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nHi\n");
            Assert.Equal(1, SubtitleTiming.Transform(path, -500));
            Assert.Equal("1\n00:00:00,500 --> 00:00:01,500\nHi\n", File.ReadAllText(path));
            Assert.Single(dir.GetFiles()); // no temporary file left behind

            var sub = Path.Combine(dir.FullName, "movie.en.sub");
            File.WriteAllText(sub, "{1}{25}Hi");
            Assert.Throws<NotSupportedException>(() => SubtitleTiming.Transform(sub, 100));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public sealed class SubtitleAlignerTests
{
    /// <summary>Irregular cue starts, like dialogue: 1.5 to 6 s apart.</summary>
    private static List<Cue> Dialogue(int count, int seed = 1)
    {
        var random = new Random(seed);
        var cues = new List<Cue>();
        var t = 5_000.0;
        for (var i = 0; i < count; i++)
        {
            cues.Add(new Cue(t, t + 1200));
            t += 1500 + random.Next(4500);
        }

        return cues;
    }

    [Fact]
    public void Finds_a_constant_offset()
    {
        var reference = Dialogue(400);
        var late = reference.Select(c => new Cue(c.Start + 2_340, c.End + 2_340)).ToList();

        var alignment = SubtitleAligner.Align(reference, late);

        Assert.NotNull(alignment);
        Assert.Equal(1, alignment.Ratio);
        Assert.InRange(alignment.OffsetMs, -2_350, -2_330);
        Assert.Equal(400, alignment.Matched);
    }

    [Fact]
    public void Finds_a_pal_framerate_conversion()
    {
        var reference = Dialogue(400);
        // Made for a 25 fps (sped-up) release: every time is shorter by 23.976/25, and 1 s late.
        var pal = reference.Select(c => new Cue(c.Start * 23.976 / 25 + 1000, c.End * 23.976 / 25 + 1000)).ToList();

        var alignment = SubtitleAligner.Align(reference, pal);

        Assert.NotNull(alignment);
        Assert.Equal(25 / 23.976, alignment.Ratio, 6);
        Assert.InRange(alignment.OffsetMs, -1_100, -1_000);
        Assert.True(alignment.MatchedFraction > 0.9);
    }

    [Fact]
    public void Tolerates_a_reference_in_another_language_with_different_cues()
    {
        var reference = Dialogue(400);
        // Every third line split or merged differently, and some jitter.
        var random = new Random(7);
        var other = reference.Where((_, i) => i % 3 != 0).Select(c => new Cue(c.Start - 4_000 + random.Next(-80, 80), c.End - 4_000)).ToList();

        var alignment = SubtitleAligner.Align(reference, other);

        Assert.NotNull(alignment);
        Assert.InRange(alignment.OffsetMs, 3_900, 4_100);
    }

    [Fact]
    public void Gives_up_on_unrelated_subtitles()
    {
        Assert.Null(SubtitleAligner.Align(Dialogue(300, seed: 1), Dialogue(300, seed: 2).Select(c => new Cue(c.Start * 1.37, c.End * 1.37)).ToList()));
        Assert.Null(SubtitleAligner.Align(Dialogue(3), Dialogue(3)));
    }
}
