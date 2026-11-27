using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Subtitles;

/// <summary>The video side of a subtitle match.</summary>
public sealed record VideoInfo
{
    public MediaType MediaType { get; init; }
    public required string Title { get; init; }
    public int Year { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    /// <summary>Original release name of the video (scene name) or its file name.</summary>
    public string? ReleaseName { get; init; }
}

public sealed record ScoredSubtitle(SubtitleCandidate Candidate, int Score, IReadOnlyList<string> Matches, IReadOnlyList<string> Rejections)
{
    public bool Acceptable => Rejections.Count == 0;
}

/// <summary>
/// Scores subtitles 0-100 by how well they fit the video. A hash match is a perfect score; otherwise points come from
/// the best of the subtitle's release names: title/series, year or season+episode, source, resolution, group and codec.
/// </summary>
public static class SubtitleScorer
{
    public static int Score(VideoInfo video, SubtitleCandidate candidate, out List<string> matches)
    {
        matches = [];
        if (candidate.HashMatch)
        {
            matches.Add("hash");
            return 100;
        }

        // Wrong episode is never acceptable, whatever the release name says.
        if (video.MediaType == MediaType.Series && candidate.SeasonNumber is { } cs && candidate.EpisodeNumber is { } ce
            && (cs != video.SeasonNumber || ce != video.EpisodeNumber))
        {
            matches.Add("wrong episode");
            return 0;
        }

        var target = ReleaseParser.Parse(video.ReleaseName ?? video.Title);
        var targetCodec = ReleaseParser.ParseVideoCodec(video.ReleaseName ?? string.Empty);
        var best = -1;
        List<string> bestMatches = [];

        foreach (var name in candidate.ReleaseNames.DefaultIfEmpty(string.Empty))
        {
            var parsed = ReleaseParser.Parse(name);
            var found = new List<string>();
            var score = 0;

            // Identity: title/series (+ year for movies, season/episode for series).
            var titleMatches = candidate.MatchedById
                || (parsed.Title is not null && ReleaseParser.NormalizeTitle(parsed.Title) == ReleaseParser.NormalizeTitle(video.Title))
                || (candidate.FeatureTitle is not null && ReleaseParser.NormalizeTitle(candidate.FeatureTitle) == ReleaseParser.NormalizeTitle(video.Title));
            if (video.MediaType == MediaType.Series)
            {
                if (titleMatches) { score += 20; found.Add("series"); }
                var season = candidate.SeasonNumber ?? parsed.SeasonNumber;
                var episode = candidate.EpisodeNumber ?? (parsed.EpisodeNumbers.Count > 0 ? parsed.EpisodeNumbers[0] : null);
                if (season == video.SeasonNumber) { score += 15; found.Add("season"); }
                if (episode == video.EpisodeNumber) { score += 15; found.Add("episode"); }
            }
            else
            {
                if (titleMatches) { score += 30; found.Add("title"); }
                var year = parsed.Year ?? candidate.FeatureYear;
                if (year is not null && video.Year > 0 && Math.Abs(year.Value - video.Year) <= 1) { score += 15; found.Add("year"); }
            }

            // Encoding details from the release name.
            var sourceWeight = video.MediaType == MediaType.Series ? 15 : 20;
            if (target.Quality.Source != QualitySource.Unknown)
            {
                if (parsed.Quality.Source == target.Quality.Source) { score += sourceWeight; found.Add("source"); }
                else if (SameFamily(parsed.Quality.Source, target.Quality.Source)) { score += sourceWeight / 2; found.Add("similar source"); }
            }

            if (target.Quality.Resolution > 0 && parsed.Quality.Resolution == target.Quality.Resolution) { score += 10; found.Add("resolution"); }
            if (target.ReleaseGroup is not null && string.Equals(parsed.ReleaseGroup, target.ReleaseGroup, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
                found.Add("release group");
            }

            if (targetCodec is not null && ReleaseParser.ParseVideoCodec(name) == targetCodec) { score += 5; found.Add("codec"); }

            if (score > best)
            {
                best = score;
                bestMatches = found;
            }
        }

        matches = bestMatches;
        return Math.Clamp(best, 0, 100);
    }

    private static bool SameFamily(QualitySource a, QualitySource b) =>
        (a is QualitySource.WebDl or QualitySource.WebRip && b is QualitySource.WebDl or QualitySource.WebRip)
        || (a is QualitySource.Bluray or QualitySource.Remux && b is QualitySource.Bluray or QualitySource.Remux);

    /// <summary>The minimum score automatic downloads need for this kind of video.</summary>
    public static int MinimumScore(VideoInfo video, SubtitleSettings settings) =>
        video.MediaType == MediaType.Series ? settings.MinimumScoreEpisodes : settings.MinimumScoreMovies;

    /// <summary>
    /// Scores all candidates for one language and ranks them, flagging those automatic downloads must skip.
    /// With <paramref name="ignoreMinimum"/>, a low score is not a rejection (the other rules still apply).
    /// </summary>
    public static IReadOnlyList<ScoredSubtitle> Rank(VideoInfo video, IEnumerable<SubtitleCandidate> candidates, SubtitleSettings settings, bool ignoreMinimum = false)
    {
        var minimum = ignoreMinimum ? 0 : MinimumScore(video, settings);
        var preferHi = settings.HearingImpaired == HearingImpairedMode.PreferHearingImpaired;

        return candidates
            .Select(c =>
            {
                var score = Score(video, c, out var matches);
                var rejections = new List<string>();
                if (score < minimum) rejections.Add($"Score {score} below minimum {minimum}");
                if (c.HearingImpaired && settings.HearingImpaired == HearingImpairedMode.ExcludeHearingImpaired) rejections.Add("Hearing impaired");
                if (c.AiTranslated && !settings.IncludeAiTranslated) rejections.Add("AI/machine translated");
                if (c.Forced) rejections.Add("Forced (foreign parts only)");
                return new ScoredSubtitle(c, score, matches, rejections);
            })
            .OrderByDescending(s => s.Acceptable)
            .ThenByDescending(s => s.Candidate.HashMatch)
            .ThenByDescending(s => s.Candidate.HearingImpaired == preferHi)
            .ThenByDescending(s => s.Score)
            .ThenByDescending(s => s.Candidate.TrustedUploader)
            .ThenByDescending(s => s.Candidate.Downloads)
            .ToList();
    }
}
