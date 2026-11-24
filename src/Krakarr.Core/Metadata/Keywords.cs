using System.Globalization;
using System.Text.Json.Serialization;
using Krakarr.Core.Configuration;

namespace Krakarr.Core.Metadata;

/// <summary>A TMDB keyword ("time travel", "heist"…) that movies and shows are tagged with.</summary>
public sealed record KeywordLookup(int Id, string Name);

/// <summary>TMDB keywords: search them by name, and resolve ids back to names.</summary>
public interface IKeywordMetadata
{
    Task<IReadOnlyList<KeywordLookup>> SearchAsync(MetadataSettings meta, string query, CancellationToken ct = default);
    Task<KeywordLookup> GetAsync(MetadataSettings meta, int id, CancellationToken ct = default);
}

public sealed class TmdbKeywords(TmdbApi api) : IKeywordMetadata
{
    /// <summary>Upper bound on the keywords one search returns; TMDB matches substrings, so short terms hit hundreds.</summary>
    public const int MaxResults = 40;

    public async Task<IReadOnlyList<KeywordLookup>> SearchAsync(MetadataSettings meta, string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var term = query.Trim();
        var result = await api.GetAsync<TmdbPaged<TmdbKeyword>>(meta, $"3/search/keyword?query={Uri.EscapeDataString(term)}&page=1", ct);
        return result.Results
            .Where(k => k.Id > 0 && !string.IsNullOrWhiteSpace(k.Name))
            .Select(k => new KeywordLookup(k.Id, k.Name!.Trim()))
            .DistinctBy(k => k.Id)
            // Exact matches first, then names starting with the term, then the rest in TMDB's order.
            .OrderBy(k => string.Equals(k.Name, term, StringComparison.OrdinalIgnoreCase) ? 0
                : k.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Take(MaxResults)
            .ToList();
    }

    public async Task<KeywordLookup> GetAsync(MetadataSettings meta, int id, CancellationToken ct = default)
    {
        var k = await api.GetAsync<TmdbKeyword>(meta, $"3/keyword/{id.ToString(CultureInfo.InvariantCulture)}", ct);
        return new KeywordLookup(k.Id > 0 ? k.Id : id, string.IsNullOrWhiteSpace(k.Name) ? $"Keyword {id}" : k.Name.Trim());
    }

    private sealed record TmdbKeyword
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }
}

/// <summary>Parses the comma-separated keyword ids that keyword discovery takes.</summary>
public static class KeywordIds
{
    /// <summary>More keywords than this rarely match anything (they're combined with AND) and only lengthen the URL.</summary>
    public const int Max = 10;

    /// <summary>Distinct positive ids in the order given, at most <see cref="Max"/>; empty when there are none.</summary>
    public static IReadOnlyList<int> Parse(string? value) =>
        (value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .Take(Max)
            .ToList();

    /// <summary>TMDB's <c>with_keywords</c> value: a comma means every keyword must match.</summary>
    public static string ToTmdb(IReadOnlyList<int> ids) => string.Join(',', ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
}
