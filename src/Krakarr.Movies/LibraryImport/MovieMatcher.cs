using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Movies.LibraryImport;

public sealed record MovieMatch(MovieLookup? Movie, MatchConfidence Confidence);

/// <summary>Picks the TMDB result that best fits a library folder.</summary>
public static class MovieMatcher
{
    public static MovieMatch Pick(ParsedFolder folder, IReadOnlyList<MovieLookup> candidates)
    {
        if (candidates.Count == 0) return new MovieMatch(null, MatchConfidence.None);

        var wanted = ReleaseParser.NormalizeTitle(folder.Title);
        MovieLookup? best = null;
        var bestConfidence = MatchConfidence.None;
        foreach (var c in candidates)
        {
            var titleMatch = wanted.Length > 0
                && (ReleaseParser.NormalizeTitle(c.Title) == wanted || ReleaseParser.NormalizeTitle(c.OriginalTitle) == wanted);
            var confidence = (titleMatch, folder.Year) switch
            {
                (true, { } y) when c.Year == y => MatchConfidence.High,
                (true, { } y) when c.Year > 0 && Math.Abs(c.Year - y) <= 1 => MatchConfidence.Medium,
                (true, null) => MatchConfidence.Medium,
                _ => MatchConfidence.None,
            };

            if (confidence > bestConfidence)
            {
                best = c;
                bestConfidence = confidence;
            }
        }

        if (best is not null) return new MovieMatch(best, bestConfidence);

        // No title match: offer the top result for review, unless the year contradicts it.
        var top = candidates[0];
        return folder.Year is { } year && top.Year > 0 && Math.Abs(top.Year - year) > 1
            ? new MovieMatch(top, MatchConfidence.Low)
            : new MovieMatch(top, folder.Year == top.Year ? MatchConfidence.Medium : MatchConfidence.Low);
    }
}
