using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krakarr.Core.Configuration;
using Krakarr.Core.Http;
using Krakarr.Data;

namespace Krakarr.Host.Integrations;

/// <summary>Critic and audience scores for one title. Each value is null when the source has no score for it.</summary>
public sealed record TitleRatings(double? Imdb, long? ImdbVotes, int? RottenTomatoes, int? Metacritic, string? RottenTomatoesUrl);

/// <summary>
/// Looks up IMDb, Rotten Tomatoes and Metacritic scores through the OMDb API (Rotten Tomatoes has no public API).
/// Results are cached in a small bounded cache so detail pages don't spend the API key's daily quota.
/// </summary>
public sealed partial class RatingsClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, TimeProvider time, ILogger<RatingsClient> logger)
{
    public const string HttpClientName = "omdb";

    /// <summary>An OMDb title is a couple of KB.</summary>
    private const long MaxResponseBytes = 256 * 1024;

    /// <summary>Upper bound for cached titles; well above what anyone browses in a day, and each entry is tiny.</summary>
    internal const int CacheCapacity = 1000;

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(24);
    private static readonly TimeSpan Failed = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (TitleRatings? Ratings, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^tt[0-9]{5,10}$", RegexOptions.IgnoreCase)]
    public static partial Regex ImdbIdPattern();

    private sealed record OmdbRating(string? Source, string? Value);

    private sealed record OmdbTitle(
        [property: JsonPropertyName("Response")] string? Response,
        [property: JsonPropertyName("Error")] string? Error,
        [property: JsonPropertyName("Ratings")] List<OmdbRating>? Ratings,
        [property: JsonPropertyName("imdbRating")] string? ImdbRating,
        [property: JsonPropertyName("imdbVotes")] string? ImdbVotes,
        [property: JsonPropertyName("Metascore")] string? Metascore,
        [property: JsonPropertyName("tomatoURL")] string? TomatoUrl);

    /// <summary>Scores for <paramref name="imdbId"/>, or null when OMDb isn't configured, doesn't know the title or can't be reached.</summary>
    public async Task<TitleRatings?> GetAsync(MetadataSettings settings, string imdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.OmdbApiKey) || !ImdbIdPattern().IsMatch(imdbId)) return null;

        var now = time.GetUtcNow();
        if (_cache.TryGetValue(imdbId, out var hit) && hit.Expires > now) return hit.Ratings;

        TitleRatings? ratings;
        bool ok;
        try
        {
            (ratings, ok) = await FetchAsync(settings.OmdbApiKey.Trim(), imdbId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not load ratings for {ImdbId} from OMDb: {Message}", imdbId, ex.Message);
            (ratings, ok) = (null, false);
        }

        Store(imdbId, ratings, now + (ok ? Fresh : Failed));
        return ratings;
    }

    private async Task<(TitleRatings? Ratings, bool Ok)> FetchAsync(string key, string imdbId, CancellationToken ct)
    {
        var baseUrl = (configuration["Omdb:BaseUrl"] ?? "https://www.omdbapi.com/").TrimEnd('/') + "/";
        var url = $"{baseUrl}?i={Uri.EscapeDataString(imdbId)}&tomatoes=true&apikey={Uri.EscapeDataString(key)}";
        using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OMDb returned HTTP {Status} for {ImdbId}", (int)response.StatusCode, imdbId);
            return (null, false);
        }

        await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
        var title = await JsonSerializer.DeserializeAsync<OmdbTitle>(body, cancellationToken: ct);
        // "Movie not found!" is a definite answer worth caching; other errors (bad key, quota) are retried sooner.
        if (title is null || !string.Equals(title.Response, "True", StringComparison.OrdinalIgnoreCase))
        {
            var notFound = title?.Error?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true;
            if (!notFound) logger.LogWarning("OMDb could not return ratings for {ImdbId}: {Error}", imdbId, title?.Error);
            return (null, notFound);
        }

        return (Parse(title), true);
    }

    private static TitleRatings Parse(OmdbTitle title)
    {
        string? Rating(string source) => title.Ratings?.FirstOrDefault(r => string.Equals(r.Source, source, StringComparison.OrdinalIgnoreCase))?.Value;

        var imdb = double.TryParse(title.ImdbRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var i) ? i : (double?)null;
        var votes = long.TryParse(title.ImdbVotes?.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (long?)null;
        var tomatoes = int.TryParse(Rating("Rotten Tomatoes")?.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : (int?)null;
        var meta = int.TryParse(title.Metascore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m
            : int.TryParse(Rating("Metacritic")?.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ? m : (int?)null;
        // Only pass on genuine Rotten Tomatoes links; the UI falls back to a search for the title.
        var tomatoUrl = Uri.TryCreate(title.TomatoUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && (u.Host == "rottentomatoes.com" || u.Host.EndsWith(".rottentomatoes.com", StringComparison.OrdinalIgnoreCase))
            ? u.ToString() : null;
        return new(imdb, votes, tomatoes, meta, tomatoUrl);
    }

    private void Store(string imdbId, TitleRatings? ratings, DateTimeOffset expires)
    {
        if (_cache.Count >= CacheCapacity && !_cache.ContainsKey(imdbId))
        {
            var now = time.GetUtcNow();
            foreach (var entry in _cache.Where(e => e.Value.Expires <= now)) _cache.TryRemove(entry.Key, out _);
            // Still full of fresh entries: drop the ones closest to expiring (the oldest).
            if (_cache.Count >= CacheCapacity)
            {
                foreach (var entry in _cache.OrderBy(e => e.Value.Expires).Take(CacheCapacity / 4).ToList()) _cache.TryRemove(entry.Key, out _);
            }
        }

        _cache[imdbId] = (ratings, expires);
    }

    internal int CachedCount => _cache.Count;
}

public static class RatingsEndpoints
{
    public static IServiceCollection AddKrakarrRatings(this IServiceCollection services)
    {
        services.AddHttpClient(RatingsClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddSingleton<RatingsClient>();
        return services;
    }

    /// <summary>
    /// <c>GET /api/v1/ratings/{imdbId}</c>: IMDb, Rotten Tomatoes and Metacritic scores, or 204 when no OMDb key is set or
    /// the scores are unavailable. Readable by every user (detail and preview pages show them).
    /// </summary>
    public static IEndpointRouteBuilder MapRatingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ratings/{imdbId}", async (string imdbId, ISettingsService settings, RatingsClient ratings, CancellationToken ct) =>
        {
            if (!RatingsClient.ImdbIdPattern().IsMatch(imdbId)) return Results.BadRequest();
            var result = await ratings.GetAsync(await settings.GetAsync<MetadataSettings>(ct), imdbId, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).WithTags("Ratings");
        return app;
    }
}
