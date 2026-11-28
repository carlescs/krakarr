using Krakarr.Core.Domain;
using Krakarr.Media.CustomFormats;
using Krakarr.Media.Parsing;

namespace Krakarr.Movies;

public sealed record ReleaseDecision
{
    public required ReleaseInfo Release { get; init; }
    public required ParsedRelease Parsed { get; init; }
    public Quality Quality => Parsed.Quality;
    /// <summary>Audio languages of the release (see <see cref="ReleaseLanguages.Resolve"/>).</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];
    /// <summary>Position of the release's best wanted language (lower is better); <see cref="int.MaxValue"/> when unwanted.</summary>
    public int LanguageRank { get; init; }
    /// <summary>Custom formats the release matches.</summary>
    public IReadOnlyList<MatchedFormat> CustomFormats { get; init; } = [];
    /// <summary>The profile's score for <see cref="CustomFormats"/>.</summary>
    public int CustomFormatScore { get; init; }
    public IReadOnlyList<string> Rejections { get; init; } = [];
    public bool Approved => Rejections.Count == 0;
}

/// <summary>Decides which releases are acceptable for a movie and ranks them.</summary>
public static class MovieDecisionEngine
{
    public static IReadOnlyList<ReleaseDecision> Evaluate(
        Movie movie,
        QualityProfile profile,
        Quality? existingFileQuality,
        IEnumerable<ReleaseInfo> releases,
        IReadOnlyList<string>? languages = null,
        CustomFormatSet? formats = null,
        int existingFormatScore = 0)
    {
        var decisions = releases.Select(r => Evaluate(movie, profile, existingFileQuality, r, languages, formats, existingFormatScore)).ToList();
        return Rank(decisions);
    }

    /// <param name="languages">Wanted audio languages, most preferred first; null or empty = any.</param>
    /// <param name="formats">The library's custom formats (null = none).</param>
    /// <param name="existingFormatScore">Custom format score of the existing file under <paramref name="profile"/>.</param>
    public static ReleaseDecision Evaluate(Movie movie, QualityProfile profile, Quality? existingFileQuality, ReleaseInfo release,
        IReadOnlyList<string>? languages = null, CustomFormatSet? formats = null, int existingFormatScore = 0)
    {
        var parsed = ReleaseParser.Parse(release.Title);
        var rejections = new List<string>();
        var releaseLanguages = ReleaseLanguages.Resolve(parsed, release);
        var languageRank = ReleaseLanguages.PreferenceRank(releaseLanguages, languages);
        var matched = formats is { Count: > 0 }
            ? formats.Match(new CustomFormatInput { Parsed = parsed, Languages = releaseLanguages, IndexerName = release.IndexerName, Size = release.Size })
            : [];
        var score = profile.FormatScore(matched.Select(m => m.Id));

        var idMatch = (release.TmdbId is { } tmdb && tmdb == movie.TmdbId)
            || (release.ImdbId is { } imdb && movie.ImdbId is not null && $"tt{imdb:D7}".Equals(movie.ImdbId, StringComparison.OrdinalIgnoreCase));

        if (!idMatch)
        {
            if (parsed.IsEpisode || parsed.IsSeasonPack)
            {
                rejections.Add("Release is a TV episode/season");
            }

            if (!TitleMatches(movie, parsed.Title))
            {
                rejections.Add($"Title mismatch (parsed \"{parsed.Title}\")");
            }

            if (parsed.Year is { } year && movie.Year > 0 && Math.Abs(year - movie.Year) > 1)
            {
                rejections.Add($"Year mismatch ({year} vs {movie.Year})");
            }
        }

        if (languageRank is null)
        {
            rejections.Add(ReleaseLanguages.Rejection(releaseLanguages, languages!));
        }

        if (parsed.Quality == Qualities.Unknown)
        {
            rejections.Add("Unknown quality");
        }
        else if (!profile.AllowedQualityIds.Contains(parsed.Quality.Id))
        {
            rejections.Add($"{parsed.Quality.Name} is not wanted in profile {profile.Name}");
        }

        var sizeMb = release.Size / 1024 / 1024;
        if (profile.MinSizeMb > 0 && release.Size > 0 && sizeMb < profile.MinSizeMb)
        {
            rejections.Add($"Too small ({sizeMb} MB < {profile.MinSizeMb} MB)");
        }

        if (profile.MaxSizeMb > 0 && sizeMb > profile.MaxSizeMb)
        {
            rejections.Add($"Too large ({sizeMb} MB > {profile.MaxSizeMb} MB)");
        }

        if (release.Seeders is 0)
        {
            rejections.Add("No seeders");
        }

        if (score < profile.MinFormatScore)
        {
            rejections.Add($"Custom format score {score} is below the minimum ({profile.MinFormatScore})");
        }

        if (existingFileQuality is not null)
        {
            var existing = FormatUpgrade.Describe(existingFileQuality, existingFormatScore);
            switch (FormatUpgrade.Evaluate(profile, existingFileQuality, existingFormatScore, parsed.Quality, score))
            {
                case UpgradeVerdict.UpgradesDisabled:
                    rejections.Add("Movie already has a file and upgrades are disabled");
                    break;
                case UpgradeVerdict.CutoffMet:
                    rejections.Add($"Existing file meets cutoff ({existing})");
                    break;
                case UpgradeVerdict.NotBetter:
                    rejections.Add($"Not an upgrade over existing {existing}");
                    break;
            }
        }

        return new ReleaseDecision
        {
            Release = release, Parsed = parsed, Rejections = rejections,
            Languages = releaseLanguages, LanguageRank = languageRank ?? int.MaxValue,
            CustomFormats = matched, CustomFormatScore = score,
        };
    }

    /// <summary>
    /// True when a parsed release title names the movie: its title, original title or one of its loaded
    /// <see cref="Movie.AlternateTitles"/>, all normalised the same way.
    /// </summary>
    public static bool TitleMatches(Movie movie, string? parsedTitle)
    {
        var normalized = ReleaseParser.NormalizeTitle(parsedTitle);
        return normalized.Length > 0
            && (normalized == ReleaseParser.NormalizeTitle(movie.Title)
                || normalized == ReleaseParser.NormalizeTitle(movie.OriginalTitle)
                || movie.AlternateTitles.Any(t => normalized == ReleaseParser.NormalizeTitle(t.Title)));
    }

    /// <summary>Approved first, then preferred language, best quality, custom format score, propers/repacks, seeders and indexer priority.</summary>
    public static IReadOnlyList<ReleaseDecision> Rank(IEnumerable<ReleaseDecision> decisions) =>
        decisions
            .OrderByDescending(d => d.Approved)
            .ThenBy(d => d.LanguageRank)
            .ThenByDescending(d => d.Quality.Weight)
            .ThenByDescending(d => d.CustomFormatScore)
            .ThenByDescending(d => d.Parsed.IsProper || d.Parsed.IsRepack)
            .ThenByDescending(d => d.Release.Seeders ?? 0)
            .ThenBy(d => d.Release.IndexerPriority)
            .ThenByDescending(d => d.Release.Size)
            .ToList();
}
