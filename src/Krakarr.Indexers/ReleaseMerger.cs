using Krakarr.Core.Domain;

namespace Krakarr.Indexers;

/// <summary>
/// De-duplicates releases across indexers as they arrive: the same info hash, or the same title and size, is one release.
/// The copy from the highest-priority indexer (lowest number) wins, then the one with more seeders, then the first seen.
/// </summary>
public sealed class ReleaseMerger
{
    private readonly Dictionary<string, ReleaseInfo> _best = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The winning copy of every distinct release so far, in first-seen order.</summary>
    public IReadOnlyCollection<ReleaseInfo> Releases => _best.Values;

    /// <summary>Merges a batch.</summary>
    /// <returns><paramref name="Added"/>: releases that are new or replace an earlier copy; <paramref name="Replaced"/>: guids of earlier copies they replace.</returns>
    public (IReadOnlyList<ReleaseInfo> Added, IReadOnlyList<string> Replaced) Add(IEnumerable<ReleaseInfo> batch)
    {
        var added = new Dictionary<string, ReleaseInfo>(StringComparer.OrdinalIgnoreCase);
        var replaced = new List<string>();
        foreach (var release in batch)
        {
            var key = Key(release);
            if (_best.TryGetValue(key, out var current))
            {
                if (!IsBetter(release, current)) continue;
                // A copy from earlier in this same batch was never handed out, so there is nothing to replace.
                if (!added.ContainsKey(key)) replaced.Add(current.Guid);
            }

            _best[key] = release;
            added[key] = release;
        }

        return (added.Values.ToList(), replaced);
    }

    private static string Key(ReleaseInfo r) => r.InfoHash ?? $"{r.Title}|{r.Size}";

    private static bool IsBetter(ReleaseInfo candidate, ReleaseInfo current) =>
        candidate.IndexerPriority != current.IndexerPriority
            ? candidate.IndexerPriority < current.IndexerPriority
            : (candidate.Seeders ?? 0) > (current.Seeders ?? 0);
}
