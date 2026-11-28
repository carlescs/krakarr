using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Http;
using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Integrations;

public enum OmdbOutcome
{
    Found,
    /// <summary>OMDb has no such title: a definite answer.</summary>
    NotFound,
    /// <summary>Unreachable, timed out or answered with something unexpected; worth trying again later.</summary>
    Failed,
    /// <summary>OMDb refused the key (invalid, or its daily limit is spent); further requests today are pointless.</summary>
    Rejected,
}

public readonly record struct OmdbLookup(OmdbOutcome Outcome, OmdbInfo? Info = null);

/// <summary>
/// Reads scores (IMDb, Rotten Tomatoes, Metacritic) and a few facts from the OMDb API by IMDb id. TMDB and TheTVDB stay
/// the metadata sources; OMDb only adds to them, through the IMDb ids they give. Live lookups (titles outside the library)
/// go through a small bounded cache so they don't spend the key's daily quota.
/// </summary>
public sealed partial class OmdbClient(IHttpClientFactory httpClientFactory, IConfiguration configuration, TimeProvider time, ILogger<OmdbClient> logger)
{
    public const string HttpClientName = "omdb";

    /// <summary>An OMDb title is a couple of KB.</summary>
    private const long MaxResponseBytes = 256 * 1024;

    /// <summary>Upper bound for cached titles; well above what anyone browses in a day, and each entry is tiny.</summary>
    internal const int CacheCapacity = 1000;

    private static readonly TimeSpan Fresh = TimeSpan.FromHours(24);
    private static readonly TimeSpan Failed = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (OmdbInfo? Info, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

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
        [property: JsonPropertyName("tomatoURL")] string? TomatoUrl,
        [property: JsonPropertyName("Rated")] string? Rated,
        [property: JsonPropertyName("Awards")] string? Awards,
        [property: JsonPropertyName("BoxOffice")] string? BoxOffice,
        [property: JsonPropertyName("Director")] string? Director,
        [property: JsonPropertyName("Writer")] string? Writer,
        [property: JsonPropertyName("Plot")] string? Plot);

    /// <summary>OMDb's view of <paramref name="imdbId"/>, or null when OMDb isn't configured, doesn't know the title or can't be reached. Cached.</summary>
    public async Task<OmdbInfo?> GetAsync(MetadataSettings settings, string imdbId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.OmdbApiKey) || !ImdbIdPattern().IsMatch(imdbId)) return null;

        if (_cache.TryGetValue(imdbId, out var hit) && hit.Expires > time.GetUtcNow()) return hit.Info;
        return (await LookupAsync(settings.OmdbApiKey, imdbId, ct)).Info;
    }

    /// <summary>Asks OMDb now (no cache read), and caches what it answers for <see cref="GetAsync"/>.</summary>
    public async Task<OmdbLookup> LookupAsync(string apiKey, string imdbId, CancellationToken ct)
    {
        if (!ImdbIdPattern().IsMatch(imdbId)) return new(OmdbOutcome.NotFound);

        OmdbLookup result;
        try
        {
            result = await FetchAsync(apiKey.Trim(), imdbId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not load {ImdbId} from OMDb: {Message}", imdbId, ex.Message);
            result = new(OmdbOutcome.Failed);
        }

        Store(imdbId, result.Info, time.GetUtcNow() + (result.Outcome is OmdbOutcome.Found or OmdbOutcome.NotFound ? Fresh : Failed));
        return result;
    }

    private async Task<OmdbLookup> FetchAsync(string key, string imdbId, CancellationToken ct)
    {
        var baseUrl = (configuration["Omdb:BaseUrl"] ?? "https://www.omdbapi.com/").TrimEnd('/') + "/";
        var url = $"{baseUrl}?i={Uri.EscapeDataString(imdbId)}&plot=full&tomatoes=true&apikey={Uri.EscapeDataString(key)}";
        using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // OMDb answers 401 both for an invalid key and for a spent daily limit.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogWarning("OMDb refused the API key for {ImdbId} (invalid key or daily limit reached)", imdbId);
            return new(OmdbOutcome.Rejected);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("OMDb returned HTTP {Status} for {ImdbId}", (int)response.StatusCode, imdbId);
            return new(OmdbOutcome.Failed);
        }

        await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
        var title = await JsonSerializer.DeserializeAsync<OmdbTitle>(body, cancellationToken: ct);
        if (title is not null && string.Equals(title.Response, "True", StringComparison.OrdinalIgnoreCase))
        {
            return new(OmdbOutcome.Found, Parse(title));
        }

        var error = title?.Error ?? string.Empty;
        // "Movie not found!" and "Incorrect IMDb ID." are definite answers; key and limit errors stop the refresh.
        if (error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("Incorrect IMDb ID", StringComparison.OrdinalIgnoreCase))
        {
            return new(OmdbOutcome.NotFound);
        }

        logger.LogWarning("OMDb could not return {ImdbId}: {Error}", imdbId, error);
        return new(error.Contains("limit", StringComparison.OrdinalIgnoreCase) || error.Contains("API key", StringComparison.OrdinalIgnoreCase)
            ? OmdbOutcome.Rejected : OmdbOutcome.Failed);
    }

    private static OmdbInfo Parse(OmdbTitle title)
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
        return new(imdb, votes, tomatoes, meta, OmdbInfo.Cut(tomatoUrl, OmdbInfo.UrlMaxLength),
            Text(title.Rated, OmdbInfo.RatedMaxLength) is { } rated && !IsUnrated(rated) ? rated : null,
            Text(title.Awards, OmdbInfo.AwardsMaxLength),
            Text(title.BoxOffice, OmdbInfo.BoxOfficeMaxLength),
            Text(title.Director, OmdbInfo.PeopleMaxLength),
            Text(title.Writer, OmdbInfo.PeopleMaxLength),
            Text(title.Plot, OmdbInfo.PlotMaxLength));
    }

    /// <summary>OMDb writes "N/A" for every missing value.</summary>
    private static string? Text(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? null : OmdbInfo.Cut(text, max);
    }

    private static bool IsUnrated(string rated) => rated.Equals("Not Rated", StringComparison.OrdinalIgnoreCase) || rated.Equals("Unrated", StringComparison.OrdinalIgnoreCase);

    private void Store(string imdbId, OmdbInfo? info, DateTimeOffset expires)
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

        _cache[imdbId] = (info, expires);
    }

    internal int CachedCount => _cache.Count;
}

/// <summary>
/// Keeps the OMDb scores and facts stored on library movies and series current, so the library can be sorted and filtered
/// by them. Runs hourly and spends at most a 24th of <see cref="MetadataSettings.OmdbDailyLimit"/> per run: titles never
/// looked up first (newly added ones), then the ones looked up longest ago.
/// </summary>
public sealed class OmdbRefreshTask(KrakarrDbContext db, ISettingsService settings, OmdbClient omdb, TimeProvider time, ILogger<OmdbRefreshTask> logger) : IScheduledTask
{
    /// <summary>Scores move slowly; a title is looked up again at most this often.</summary>
    internal static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    /// <summary>Failed lookups in a row after which a run gives up (OMDb is down); one odd title doesn't stop the others.</summary>
    private const int MaxFailures = 3;

    public string Name => "Refresh OMDb ratings";
    public TimeSpan Interval => TimeSpan.FromHours(1);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(3);

    public async ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken)
    {
        var meta = await settings.GetAsync<MetadataSettings>(cancellationToken);
        return string.IsNullOrWhiteSpace(meta.OmdbApiKey) || meta.OmdbDailyLimit <= 0 ? null : Interval;
    }

    private sealed record Due(int Id, bool IsMovie, string ImdbId, DateTimeOffset? Updated);

    /// <summary>Requests one run may make.</summary>
    public static int Budget(int dailyLimit) => dailyLimit <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(dailyLimit / 24.0));

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var meta = await settings.GetAsync<MetadataSettings>(cancellationToken);
        // Run manually with a limit of 0, still look up a few so "Run now" does something.
        var budget = Budget(meta.OmdbDailyLimit > 0 ? meta.OmdbDailyLimit : 24);
        if (string.IsNullOrWhiteSpace(meta.OmdbApiKey)) return;

        var staleBefore = time.GetUtcNow() - RefreshAfter;
        var movies = await db.Movies.AsNoTracking()
            .Where(m => m.ImdbId != null && (m.OmdbUpdated == null || m.OmdbUpdated < staleBefore))
            .OrderBy(m => m.OmdbUpdated == null ? 0 : 1).ThenBy(m => m.OmdbUpdated).ThenBy(m => m.Id)
            .Select(m => new Due(m.Id, true, m.ImdbId!, m.OmdbUpdated))
            .Take(budget)
            .ToListAsync(cancellationToken);
        var series = await db.Series.AsNoTracking()
            .Where(s => s.ImdbId != null && (s.OmdbUpdated == null || s.OmdbUpdated < staleBefore))
            .OrderBy(s => s.OmdbUpdated == null ? 0 : 1).ThenBy(s => s.OmdbUpdated).ThenBy(s => s.Id)
            .Select(s => new Due(s.Id, false, s.ImdbId!, s.OmdbUpdated))
            .Take(budget)
            .ToListAsync(cancellationToken);
        var due = movies.Concat(series).OrderBy(d => d.Updated ?? DateTimeOffset.MinValue).Take(budget).ToList();
        if (due.Count == 0) return;

        var updated = 0;
        var failures = 0;
        foreach (var item in due)
        {
            var lookup = await omdb.LookupAsync(meta.OmdbApiKey, item.ImdbId, cancellationToken);
            if (lookup.Outcome is OmdbOutcome.Rejected || (lookup.Outcome is OmdbOutcome.Failed && ++failures >= MaxFailures))
            {
                // A refused key or an unreachable OMDb fails every remaining lookup too; the next run tries again.
                logger.LogWarning("OMDb refresh stopped after {Updated} title(s): {Outcome}", updated, lookup.Outcome);
                break;
            }

            if (lookup.Outcome is OmdbOutcome.Failed) continue;
            failures = 0;

            db.ChangeTracker.Clear();
            IOmdbFields? entity = item.IsMovie
                ? await db.Movies.FirstOrDefaultAsync(m => m.Id == item.Id, cancellationToken)
                : await db.Series.FirstOrDefaultAsync(s => s.Id == item.Id, cancellationToken);
            // Removed meanwhile, or a refresh gave it another IMDb id: leave it for the next run.
            if (entity is null || entity.ImdbId != item.ImdbId) continue;

            OmdbInfo.Apply(entity, lookup.Info, time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            updated++;
        }

        db.ChangeTracker.Clear();
        logger.LogInformation("OMDb refresh: updated {Updated} of {Due} title(s)", updated, due.Count);
    }
}

public static class OmdbEndpoints
{
    public static IServiceCollection AddKrakarrOmdb(this IServiceCollection services)
    {
        services.AddHttpClient(OmdbClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddSingleton<OmdbClient>();
        services.AddScheduledTask<OmdbRefreshTask>();
        return services;
    }

    /// <summary>
    /// <c>GET /api/v1/ratings/{imdbId}</c>: OMDb scores and facts (<see cref="OmdbInfo"/>), or 204 when no OMDb key is set or
    /// OMDb has nothing. Readable by every user: detail and preview pages show them. Library titles carry a stored copy in
    /// their own DTO, so pages only call this for titles outside the library or not looked up yet.
    /// </summary>
    public static IEndpointRouteBuilder MapOmdbEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/ratings/{imdbId}", async (string imdbId, ISettingsService settings, OmdbClient omdb, CancellationToken ct) =>
        {
            if (!OmdbClient.ImdbIdPattern().IsMatch(imdbId)) return Results.BadRequest();
            var result = await omdb.GetAsync(await settings.GetAsync<MetadataSettings>(ct), imdbId, ct);
            return result is null ? Results.NoContent() : Results.Ok(result);
        }).WithTags("Ratings");
        return app;
    }
}
