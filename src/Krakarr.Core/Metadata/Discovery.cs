using System.Text.Json.Serialization;

namespace Krakarr.Core.Metadata;

/// <summary>One page of a TMDB discovery list (trending, popular, upcoming…).</summary>
public sealed record DiscoverPage<T>(IReadOnlyList<T> Results, int Page, int TotalPages);

/// <summary>TMDB's paged list envelope ({"page":1,"results":[…],"total_pages":N}).</summary>
public sealed record TmdbPaged<T>
{
    [JsonPropertyName("page")] public int Page { get; init; } = 1;
    [JsonPropertyName("total_pages")] public int TotalPages { get; init; } = 1;
    [JsonPropertyName("results")] public List<T> Results { get; init; } = [];
}

/// <summary>
/// Blends the TMDB recommendation lists of several library titles into one "for you" list: a title that several of
/// your titles point at outranks one that only a single title suggests, and earlier positions in each list weigh more.
/// </summary>
public static class RecommendationRanker
{
    /// <summary>Positions past this in a single list add nothing.</summary>
    private const int Depth = 20;

    /// <param name="lists">Recommendation lists, one per seed title, most relevant seed first.</param>
    /// <param name="key">Identity of an item (its TMDB id).</param>
    /// <param name="exclude">Keys to leave out, e.g. titles already in the library.</param>
    /// <param name="take">Maximum number of items to return.</param>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<IReadOnlyList<T>> lists, Func<T, int> key, IReadOnlySet<int> exclude, int take)
    {
        var scores = new Dictionary<int, (T Item, double Score, int Seeds, int Order)>();
        var order = 0;
        foreach (var list in lists)
        {
            var seen = new HashSet<int>();
            for (var rank = 0; rank < Math.Min(list.Count, Depth); rank++)
            {
                var item = list[rank];
                var id = key(item);
                if (exclude.Contains(id) || !seen.Add(id)) continue;
                var weight = (Depth - rank) / (double)Depth;
                scores[id] = scores.TryGetValue(id, out var s)
                    ? (s.Item, s.Score + weight, s.Seeds + 1, s.Order)
                    : (item, weight, 1, order++);
            }
        }

        return scores.Values
            .OrderByDescending(s => s.Seeds)
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Order)
            .Take(take)
            .Select(s => s.Item)
            .ToList();
    }
}
