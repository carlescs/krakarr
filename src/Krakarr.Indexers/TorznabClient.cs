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
    /// <summary>
    /// Other names of the title, ready to use as text queries (movies include the year). An indexer that cannot search
    /// by id is also queried with the ones in its default language (see <see cref="TorznabClient.MaxAlternateQueries"/>).
    /// </summary>
    public IReadOnlyList<AlternateTitleInfo> AlternateTitles { get; init; } = [];
}

public interface ITorznabClient
{
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default);
    Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default);
}

public sealed class TorznabClient(IHttpClientFactory httpClientFactory, ILogger<TorznabClient> logger) : ITorznabClient
{
    public const string HttpClientName = "indexers";

    /// <summary>Most extra text queries per indexer and search for alternate titles.</summary>
    public const int MaxAlternateQueries = 2;

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
        var releases = TorznabParser.ParseResults(await GetXmlAsync(BuildUrl(indexer, parameters), ct), indexer);
        var alternates = AlternateQueries(indexer, criteria, caps);
        if (alternates.Count == 0)
        {
            return releases;
        }

        // Local-language trackers name releases by their local title; the main query alone misses them.
        var all = releases.ToList();
        var seen = all.Select(r => r.Guid).ToHashSet(StringComparer.Ordinal);
        foreach (var query in alternates)
        {
            try
            {
                var extra = BuildSearchParameters(indexer, criteria with { Query = query, ImdbId = null, TmdbId = null, TvdbId = null }, caps);
                all.AddRange(TorznabParser.ParseResults(await GetXmlAsync(BuildUrl(indexer, extra), ct), indexer).Where(r => seen.Add(r.Guid)));
            }
            catch (Exception ex) when (ex is IndexerException or HttpRequestException)
            {
                // The main query worked; a failed extra query only loses its own results.
                logger.LogDebug("Alternate title search \"{Query}\" on {Indexer} failed: {Message}", query, indexer.Name, ex.Message);
            }
        }

        return all;
    }

    /// <summary>
    /// Extra text queries for an indexer that cannot search by any of the criteria's ids: alternate titles in its default
    /// language (or of unknown language), different from the main query.
    /// </summary>
    internal static IReadOnlyList<string> AlternateQueries(Indexer indexer, SearchCriteria criteria, IndexerCapabilities caps)
    {
        if (criteria.Rss || criteria.AlternateTitles.Count == 0 || UsesIdSearch(criteria, caps))
        {
            return [];
        }

        return Media.Parsing.AlternateTitles.ForIndexer(criteria.AlternateTitles, indexer.DefaultLanguage, MaxAlternateQueries + 1)
            .Where(q => !string.Equals(q, criteria.Query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxAlternateQueries)
            .ToList();
    }

    /// <summary>True when the search mode the indexer offers for the criteria takes one of its ids.</summary>
    internal static bool UsesIdSearch(SearchCriteria criteria, IndexerCapabilities caps)
    {
        var supported = SearchMode(criteria, caps).Supported;
        return (!string.IsNullOrEmpty(criteria.ImdbId) && supported.Contains("imdbid"))
            || (criteria.TmdbId is not null && supported.Contains("tmdbid"))
            || (criteria.TvdbId is not null && supported.Contains("tvdbid"));
    }

    private static (string Mode, IReadOnlySet<string> Supported) SearchMode(SearchCriteria criteria, IndexerCapabilities caps)
    {
        if (criteria.Type == SearchType.Movie && caps.MovieSearchAvailable) return ("movie", caps.MovieSearchParams);
        if (criteria.Type == SearchType.Tv && caps.TvSearchAvailable) return ("tvsearch", caps.TvSearchParams);
        return ("search", new HashSet<string> { "q" });
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

        var (mode, supported) = SearchMode(criteria, caps);

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
