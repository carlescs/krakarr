using Krakarr.Core.Domain;
using Krakarr.Media.Parsing;

namespace Krakarr.Series.LibraryImport;

public sealed record SeriesMatch(SeriesLookup? Series, MatchConfidence Confidence);

/// <summary>Picks the TheTVDB result that best fits a library folder.</summary>
public static class SeriesMatcher
{
    public static SeriesMatch Pick(ParsedFolder folder, IReadOnlyList<SeriesLookup> candidates)
    {
        if (folder.TvdbId is { } tvdb)
        {
            var byId = candidates.FirstOrDefault(c => c.TvdbId == tvdb)
                ?? new SeriesLookup { TvdbId = tvdb, Title = folder.Title, Year = folder.Year ?? 0 };
            return new SeriesMatch(byId, MatchConfidence.Exact);
        }

        if (candidates.Count == 0) return new SeriesMatch(null, MatchConfidence.None);

        // "The Office (US)" folders must match "The Office (US)" — keep the suffix, only drop a trailing year.
        var wanted = SeriesNaming.NormalizedTitle(folder.Title);
        SeriesLookup? best = null;
        var bestConfidence = MatchConfidence.None;
        foreach (var c in candidates)
        {
            var titleMatch = wanted.Length > 0 && SeriesNaming.NormalizedTitle(c.Title) == wanted;
            var confidence = (titleMatch, folder.Year) switch
            {
                (true, { } y) when c.Year == y => MatchConfidence.High,
                (true, { } y) when c.Year > 0 && Math.Abs(c.Year - y) <= 1 => MatchConfidence.Medium,
                (true, null) => candidates.Count(x => SeriesNaming.NormalizedTitle(x.Title) == wanted) == 1 ? MatchConfidence.High : MatchConfidence.Medium,
                _ => MatchConfidence.None,
            };

            if (confidence > bestConfidence)
            {
                best = c;
                bestConfidence = confidence;
            }
        }

        return best is not null ? new SeriesMatch(best, bestConfidence) : new SeriesMatch(candidates[0], MatchConfidence.Low);
    }
}
