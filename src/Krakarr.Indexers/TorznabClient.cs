using System.Collections.Concurrent;
using System.Globalization;
using System.Xml.Linq;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Microsoft.Extensions.Logging;

namespace Krakarr.Indexers;

public enum SearchType
{
    Generic,
    Movie,
    Tv,
}

public sealed record SearchCriteria
{
    public SearchType Type { get; init; } = SearchType.Generic;
    public string? Query { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public int? TvdbId { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int Limit { get; init; } = 100;
    /// <summary>When true, fetch the latest releases (RSS) instead of searching.</summary>
    public bool Rss { get; init; }
}

public interface ITorznabClient
{
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default);
    Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default);
}

public sealed class TorznabClient(IHttpClientFactory httpClientFactory, ILogger<TorznabClient> logger) : ITorznabClient
{
    public const string HttpClientName = "indexers";

    private static readonly ConcurrentDictionary<string, (IndexerCapabilities Caps, DateTimeOffset Fetched)> CapsCache = new();

    public async Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default)
    {
        var key = CacheKey(indexer);
        if (!forceRefresh && CapsCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.Fetched < TimeSpan.FromHours(6))
        {
            return cached.Caps;
        }

        var caps = TorznabParser.ParseCapabilities(await GetXmlAsync(BuildUrl(indexer, [("t", "caps")]), ct));
        CapsCache[key] = (caps, DateTimeOffset.UtcNow);
        return caps;
    }

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default)
    {
        IndexerCapabilities caps;
        try
        {
            caps = await GetCapabilitiesAsync(indexer, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read capabilities for {Indexer}; falling back to generic search", indexer.Name);
            caps = new IndexerCapabilities();
        }

        var parameters = BuildSearchParameters(indexer, criteria, caps);
        return TorznabParser.ParseResults(await GetXmlAsync(BuildUrl(indexer, parameters), ct), indexer);
    }

    internal static List<(string Key, string Value)> BuildSearchParameters(Indexer indexer, SearchCriteria criteria, IndexerCapabilities caps)
    {
        var p = new List<(string, string)>();
        var categories = criteria.Type switch
        {
            SearchType.Movie => indexer.MovieCategories,
            SearchType.Tv => indexer.TvCategories,
            _ => [],
        };

        var mode = "search";
        IReadOnlySet<string> supported = new HashSet<string> { "q" };
        if (criteria.Type == SearchType.Movie && caps.MovieSearchAvailable)
        {
            mode = "movie";
            supported = caps.MovieSearchParams;
        }
        else if (criteria.Type == SearchType.Tv && caps.TvSearchAvailable)
        {
            mode = "tvsearch";
            supported = caps.TvSearchParams;
        }

        p.Add(("t", mode));
        p.Add(("extended", "1"));
        p.Add(("limit", criteria.Limit.ToString(CultureInfo.InvariantCulture)));
        if (categories.Count > 0)
        {
            p.Add(("cat", string.Join(',', categories)));
        }

        if (criteria.Rss)
        {
            return p;
        }

        var usedId = false;
        if (!string.IsNullOrEmpty(criteria.ImdbId) && supported.Contains("imdbid"))
        {
            p.Add(("imdbid", criteria.ImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? criteria.ImdbId[2..] : criteria.ImdbId));
            usedId = true;
        }
        else if (criteria.TmdbId is { } tmdb && supported.Contains("tmdbid"))
        {
            p.Add(("tmdbid", tmdb.ToString(CultureInfo.InvariantCulture)));
            usedId = true;
        }
        else if (criteria.TvdbId is { } tvdb && supported.Contains("tvdbid"))
        {
            p.Add(("tvdbid", tvdb.ToString(CultureInfo.InvariantCulture)));
            usedId = true;
        }

        if (!usedId && !string.IsNullOrWhiteSpace(criteria.Query))
        {
            var query = criteria.Query;
            // Without a tv-search mode the episode has to be part of the text query ("Show S01E02").
            if (criteria.Type == SearchType.Tv && mode != "tvsearch" && criteria.Season is { } s)
            {
                query += criteria.Episode is { } e
                    ? $" S{s.ToString("00", CultureInfo.InvariantCulture)}E{e.ToString("00", CultureInfo.InvariantCulture)}"
                    : $" S{s.ToString("00", CultureInfo.InvariantCulture)}";
            }

            p.Add(("q", query));
        }

        if (criteria.Season is { } season && mode == "tvsearch")
        {
            p.Add(("season", season.ToString(CultureInfo.InvariantCulture)));
            if (criteria.Episode is { } ep)
            {
                p.Add(("ep", ep.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return p;
    }

    internal static Uri BuildUrl(Indexer indexer, IEnumerable<(string Key, string Value)> parameters)
    {
        var baseUrl = indexer.BaseUrl.TrimEnd('/');
        var path = string.IsNullOrEmpty(indexer.ApiPath) ? string.Empty : "/" + indexer.ApiPath.Trim('/');
        var all = parameters.ToList();
        if (!string.IsNullOrEmpty(indexer.ApiKey))
        {
            all.Add(("apikey", indexer.ApiKey));
        }

        var query = string.Join('&', all.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new Uri($"{baseUrl}{path}?{query}");
    }

    private async Task<XDocument> GetXmlAsync(Uri url, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new IndexerException($"Indexer responded with HTTP {(int)response.StatusCode}");
        }

        try
        {
            await using var body = await response.Content.ReadAsBoundedStreamAsync(BoundedContent.MaxIndexerResponseBytes, ct);
            return await TorznabParser.LoadAsync(body, ct);
        }
        catch (ResponseTooLargeException ex)
        {
            throw new IndexerException(ex.Message, ex);
        }
    }

    private static string CacheKey(Indexer indexer) => $"{indexer.Id}|{indexer.BaseUrl}|{indexer.ApiPath}";
}
