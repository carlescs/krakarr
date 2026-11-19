using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Movies;

public sealed record ReleaseDecision
{
    public required ReleaseInfo Release { get; init; }
    public required ParsedRelease Parsed { get; init; }
    public Quality Quality => Parsed.Quality;
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
        IEnumerable<ReleaseInfo> releases)
    {
        var decisions = releases.Select(r => Evaluate(movie, profile, existingFileQuality, r)).ToList();
        return Rank(decisions);
    }

    public static ReleaseDecision Evaluate(Movie movie, QualityProfile profile, Quality? existingFileQuality, ReleaseInfo release)
    {
        var parsed = ReleaseParser.Parse(release.Title);
        var rejections = new List<string>();

        var idMatch = (release.TmdbId is { } tmdb && tmdb == movie.TmdbId)
            || (release.ImdbId is { } imdb && movie.ImdbId is not null && $"tt{imdb:D7}".Equals(movie.ImdbId, StringComparison.OrdinalIgnoreCase));

        if (!idMatch)
        {
            if (parsed.IsEpisode || parsed.IsSeasonPack)
            {
                rejections.Add("Release is a TV episode/season");
            }

            var normalized = ReleaseParser.NormalizeTitle(parsed.Title);
            var titleMatches = normalized.Length > 0
                && (normalized == ReleaseParser.NormalizeTitle(movie.Title)
                    || normalized == ReleaseParser.NormalizeTitle(movie.OriginalTitle));
            if (!titleMatches)
            {
                rejections.Add($"Title mismatch (parsed \"{parsed.Title}\")");
            }

            if (parsed.Year is { } year && movie.Year > 0 && Math.Abs(year - movie.Year) > 1)
            {
                rejections.Add($"Year mismatch ({year} vs {movie.Year})");
            }
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

        if (existingFileQuality is not null)
        {
            var cutoff = Qualities.FindById(profile.CutoffQualityId);
            if (!profile.UpgradeAllowed)
            {
                rejections.Add("Movie already has a file and upgrades are disabled");
            }
            else if (existingFileQuality.Weight >= cutoff.Weight)
            {
                rejections.Add($"Existing file meets cutoff ({existingFileQuality.Name})");
            }
            else if (parsed.Quality.Weight <= existingFileQuality.Weight)
            {
                rejections.Add($"Not an upgrade over existing {existingFileQuality.Name}");
            }
        }

        return new ReleaseDecision { Release = release, Parsed = parsed, Rejections = rejections };
    }

    /// <summary>Approved first, then best quality, propers/repacks, seeders and indexer priority.</summary>
    public static IReadOnlyList<ReleaseDecision> Rank(IEnumerable<ReleaseDecision> decisions) =>
        decisions
            .OrderByDescending(d => d.Approved)
            .ThenByDescending(d => d.Quality.Weight)
            .ThenByDescending(d => d.Parsed.IsProper || d.Parsed.IsRepack)
            .ThenByDescending(d => d.Release.Seeders ?? 0)
            .ThenBy(d => d.Release.IndexerPriority)
            .ThenByDescending(d => d.Release.Size)
            .ToList();
}
