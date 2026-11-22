using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;

namespace Krakarr.Subtitles.Tests;

public class HashAndLanguageTests
{
    [Theory]
    [InlineData(0, "0000000000020000")]
    [InlineData(1, "a0601fdf9f620d40")]
    [InlineData(2, "050613141f2c30a8")]
    public void Computes_opensubtitles_hash(int vector, string expected)
    {
        var data = vector switch
        {
            0 => new byte[131072],
            1 => Enumerable.Range(0, 200000).Select(i => (byte)(i % 256)).ToArray(),
            _ => Enumerable.Range(0, 1000).Select(i => (byte)((i * 7 + 3) % 251)).ToArray(),
        };
        using var stream = new MemoryStream(data);
        Assert.Equal(expected, OpenSubtitlesHash.Compute(stream));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("English", "en")]
    [InlineData("pt-BR", "pt-br")]
    [InlineData("pob", "pt-br")]
    [InlineData("spa", "es")]
    [InlineData("klingon", null)]
    public void Normalizes_languages(string input, string? expected) => Assert.Equal(expected, Languages.Normalize(input));

    [Theory]
    [InlineData("Movie (2020).en.srt", "en", false, false)]
    [InlineData("Movie (2020).eng.sdh.srt", "en", true, false)]
    [InlineData("Movie (2020).Spanish.forced.srt", "es", false, true)]
    [InlineData("Movie (2020).hi.es.ass", "es", true, false)]
    public void Reads_sidecar_names(string file, string language, bool hi, bool forced)
    {
        var info = Languages.FromSidecarName(file, "Movie (2020)");
        Assert.NotNull(info);
        Assert.Equal((language, hi, forced), info.Value);
    }

    [Fact]
    public void Ignores_sidecars_without_language_or_of_other_videos()
    {
        Assert.Null(Languages.FromSidecarName("Movie (2020).srt", "Movie (2020)"));
        Assert.Null(Languages.FromSidecarName("Other.en.srt", "Movie (2020)"));
    }

    [Fact]
    public void Builds_subtitle_paths()
    {
        var dir = $"{Path.DirectorySeparatorChar}m";
        Assert.Equal(Path.Combine(dir, "Movie (2020).en.srt"), SubtitleService.SubtitlePath("/m/Movie (2020).mkv", "en", false, false, ".srt"));
        Assert.Equal(Path.Combine(dir, "Movie (2020).pt-br.hi.forced.ass"), SubtitleService.SubtitlePath("/m/Movie (2020).mkv", "pt-br", true, true, "ass"));
    }
}

public class SubtitleScorerTests
{
    private static readonly VideoInfo Matrix = new()
    {
        MediaType = MediaType.Movie, Title = "The Matrix", Year = 1999, ReleaseName = "The.Matrix.1999.1080p.BluRay.x264-SPARKS",
    };

    private static readonly VideoInfo Episode = new()
    {
        MediaType = MediaType.Series, Title = "The Office", Year = 2005, SeasonNumber = 2, EpisodeNumber = 3,
        ReleaseName = "The.Office.S02E03.720p.WEB-DL.DD5.1.H.264-NTb",
    };

    private static SubtitleCandidate C(string release, string lang = "en", bool hi = false, bool hash = false, bool byId = true,
        int? season = null, int? episode = null, bool ai = false, int downloads = 0) => new()
    {
        Provider = "test", Payload = release, Language = lang, ReleaseNames = [release], HearingImpaired = hi, HashMatch = hash,
        MatchedById = byId, SeasonNumber = season, EpisodeNumber = episode, AiTranslated = ai, Downloads = downloads,
    };

    [Fact]
    public void Hash_match_is_perfect()
    {
        Assert.Equal(100, SubtitleScorer.Score(Matrix, C("whatever", hash: true), out var m));
        Assert.Equal(["hash"], m);
    }

    [Fact]
    public void Exact_release_scores_full_marks()
    {
        Assert.Equal(100, SubtitleScorer.Score(Matrix, C("The.Matrix.1999.1080p.BluRay.x264-SPARKS"), out _));
    }

    [Fact]
    public void Partial_matches_score_proportionally()
    {
        // title 30 + year 15 + source family 10 + resolution 10 + codec 5 = 70 (different group, Remux vs BluRay)
        Assert.Equal(70, SubtitleScorer.Score(Matrix, C("The.Matrix.1999.1080p.BluRay.REMUX.AVC-FGT"), out var m));
        Assert.Contains("similar source", m);
        Assert.DoesNotContain("release group", m);
        // Only title (by id) and year
        Assert.Equal(45, SubtitleScorer.Score(Matrix, C("The.Matrix.1999.DVDRip.XviD-AMIABLE"), out _));
    }

    [Fact]
    public void Episodes_require_matching_season_and_episode()
    {
        Assert.Equal(100, SubtitleScorer.Score(Episode, C("The.Office.S02E03.720p.WEB-DL.DD5.1.H.264-NTb", season: 2, episode: 3), out _));
        Assert.Equal(0, SubtitleScorer.Score(Episode, C("The.Office.S02E03.720p.WEB-DL.DD5.1.H.264-NTb", season: 2, episode: 4), out var m));
        Assert.Contains("wrong episode", m);
        // WEBRip is the same family as WEB-DL: half the source points.
        Assert.Equal(20 + 15 + 15 + 7 + 10 + 5, SubtitleScorer.Score(Episode, C("The.Office.S02E03.720p.WEBRip.x264-GRP", season: 2, episode: 3), out _));
    }

    [Fact]
    public void Ranking_applies_preferences_and_minimum_score()
    {
        var settings = new SubtitleSettings { MinimumScoreMovies = 60 };
        var ranked = SubtitleScorer.Rank(Matrix,
        [
            C("The.Matrix.1999.1080p.BluRay.x264-SPARKS", hi: true, downloads: 10),
            C("The.Matrix.1999.1080p.BluRay.x264-OTHER", downloads: 5),
            C("The.Matrix.1999.DVDRip.XviD-AMIABLE"),
            C("The.Matrix.1999.1080p.BluRay.x264-SPARKS", ai: true),
        ], settings);

        // Normal subtitles beat hearing-impaired ones by default, even with a slightly lower score.
        Assert.Equal("The.Matrix.1999.1080p.BluRay.x264-OTHER", ranked[0].Candidate.Payload);
        Assert.True(ranked[1].Candidate.HearingImpaired);
        Assert.Contains(ranked, r => r.Candidate.AiTranslated && r.Rejections.Contains("AI/machine translated"));
        Assert.Contains(ranked, r => r.Candidate.Payload.Contains("DVDRip") && r.Rejections.Any(x => x.StartsWith("Score 45 below")));

        var preferHi = SubtitleScorer.Rank(Matrix, [C("The.Matrix.1999.1080p.BluRay.x264-OTHER"), C("The.Matrix.1999.1080p.BluRay.x264-SPARKS", hi: true)],
            settings with { HearingImpaired = HearingImpairedMode.PreferHearingImpaired });
        Assert.True(preferHi[0].Candidate.HearingImpaired);

        var excludeHi = SubtitleScorer.Rank(Matrix, [C("The.Matrix.1999.1080p.BluRay.x264-SPARKS", hi: true)],
            settings with { HearingImpaired = HearingImpairedMode.ExcludeHearingImpaired });
        Assert.False(excludeHi[0].Acceptable);
    }
}
