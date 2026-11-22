using System.Text.Json;
using Krakarr.Core.Http;

namespace Krakarr.Indexers;

/// <summary>Where a Prowlarr instance lives and how to authenticate against it.</summary>
public sealed record ProwlarrConnection(string Url, string ApiKey);

/// <summary>One indexer configured in Prowlarr, reduced to what an import needs.</summary>
public sealed record ProwlarrIndexer(
    int Id,
    string Name,
    string Protocol,
    bool Enabled,
    int Priority,
    bool SupportsRss,
    IReadOnlyList<IndexerCategory> Categories)
{
    /// <summary>Only torrent indexers can be grabbed from; Krakarr has no usenet download client.</summary>
    public bool Supported => string.Equals(Protocol, "torrent", StringComparison.OrdinalIgnoreCase);
}

public interface IProwlarrClient
{
    Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(ProwlarrConnection connection, CancellationToken ct = default);
}

/// <summary>
/// Reads the indexers configured in Prowlarr (<c>GET /api/v1/indexer</c>). Prowlarr serves each of them as a Torznab
/// feed at <c>{url}/{id}/api</c> with its own API key, which is how imported indexers are searched afterwards.
/// </summary>
public sealed class ProwlarrClient(IHttpClientFactory httpClientFactory) : IProwlarrClient
{
    /// <summary>The indexer list carries every indexer's full definition and category tree; a few MB at most.</summary>
    private const long MaxResponseBytes = 16 * 1024 * 1024;

    public async Task<IReadOnlyList<ProwlarrIndexer>> GetIndexersAsync(ProwlarrConnection connection, CancellationToken ct = default)
    {
        var http = httpClientFactory.CreateClient(TorznabClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{NormalizeUrl(connection.Url)}/api/v1/indexer");
        request.Headers.Add("X-Api-Key", connection.ApiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new IndexerException("Prowlarr rejected the API key");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"Prowlarr responded with HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
            return await ParseAsync(body, ct);
        }
        catch (ResponseTooLargeException ex)
        {
            throw new IndexerException(ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new IndexerException("The response is not a Prowlarr indexer list. Check the URL.", ex);
        }
    }

    internal static async Task<IReadOnlyList<ProwlarrIndexer>> ParseAsync(Stream body, CancellationToken ct)
    {
        var items = await JsonSerializer.DeserializeAsync<List<IndexerResource>>(body, JsonSerializerOptions.Web, ct) ?? [];
        return items
            .Select(i => new ProwlarrIndexer(
                i.Id,
                i.Name ?? $"Prowlarr #{i.Id}",
                i.Protocol ?? "unknown",
                i.Enable,
                i.Priority is >= 1 and <= 50 ? i.Priority : 25,
                i.SupportsRss,
                FlattenCategories(i.Capabilities?.Categories)))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<IndexerCategory> FlattenCategories(List<CategoryResource>? categories)
    {
        var result = new List<IndexerCategory>();
        foreach (var c in categories ?? [])
        {
            result.Add(new IndexerCategory(c.Id, c.Name ?? c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            foreach (var sub in c.SubCategories ?? [])
            {
                result.Add(new IndexerCategory(sub.Id, sub.Name ?? sub.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Id));
            }
        }

        return result;
    }

    /// <summary>"http://host:9696/" → "http://host:9696".</summary>
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    /// <summary>Base URL of the Torznab feed Prowlarr serves for one of its indexers (the API path "/api" is appended).</summary>
    public static string FeedUrl(string prowlarrUrl, int indexerId) => $"{NormalizeUrl(prowlarrUrl)}/{indexerId}";

    /// <summary>Standard movie / TV categories the indexer offers: its top-level ones in the range, else any in it.</summary>
    public static List<int> SuggestCategories(IReadOnlyList<IndexerCategory> categories, int min, int max, int fallback)
    {
        var inRange = categories.Where(c => c.Id >= min && c.Id <= max).ToList();
        var parents = inRange.Where(c => c.ParentId is null).ToList();
        var picked = (parents.Count > 0 ? parents : inRange).Select(c => c.Id).Distinct().ToList();
        return picked.Count > 0 ? picked : [fallback];
    }

    private sealed class IndexerResource
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Protocol { get; set; }
        public bool Enable { get; set; }
        public int Priority { get; set; }
        public bool SupportsRss { get; set; }
        public CapabilitiesResource? Capabilities { get; set; }
    }

    private sealed class CapabilitiesResource
    {
        public List<CategoryResource>? Categories { get; set; }
    }

    private sealed class CategoryResource
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public List<CategoryResource>? SubCategories { get; set; }
    }
}
