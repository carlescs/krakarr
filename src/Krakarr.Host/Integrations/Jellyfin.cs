using System.Net.Http.Headers;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Http;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Integrations;

public enum JellyfinItemKind
{
    Movie,
    Series,
}

/// <summary>What Krakarr knows about a title, used to find the same title on the Jellyfin server.</summary>
public sealed record JellyfinLookup(JellyfinItemKind Kind, string Title, string? OriginalTitle, int Year, int? TmdbId, int? TvdbId, string? ImdbId);

public sealed record JellyfinItem(string Id, string? Name, string? ServerId, int? ProductionYear, Dictionary<string, string?>? ProviderIds);

public sealed record JellyfinTestResult(bool Success, string? ServerName = null, string? Version = null, string? Message = null);

/// <summary>Finds library titles on a Jellyfin server and builds links to their pages in the Jellyfin web client.</summary>
public sealed class JellyfinClient(IHttpClientFactory httpClientFactory, ILogger<JellyfinClient> logger)
{
    public const string HttpClientName = "jellyfin";

    /// <summary>Search results are capped at <see cref="SearchLimit"/> slim items; this bound is generous for that.</summary>
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private const int SearchLimit = 25;

    private sealed record ItemsResponse(List<JellyfinItem>? Items);

    private sealed record SystemInfo(string? ServerName, string? Version);

    /// <summary>Link to the title's page in Jellyfin, or null when Jellyfin is not configured or doesn't have it.</summary>
    public async Task<string?> FindItemUrlAsync(JellyfinSettings settings, JellyfinLookup lookup, CancellationToken ct)
    {
        if (!settings.IsConfigured) return null;

        foreach (var term in new[] { lookup.Title, lookup.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var query = $"Items?Recursive=true&IncludeItemTypes={lookup.Kind}&SearchTerm={Uri.EscapeDataString(term!)}"
                + $"&Fields=ProviderIds&EnableImages=false&EnableUserData=false&Limit={SearchLimit}";
            var response = await GetAsync<ItemsResponse>(settings, query, ct);
            if (Match(response?.Items ?? [], lookup) is { } item) return ItemUrl(settings, item);
        }

        return null;
    }

    public async Task<JellyfinTestResult> TestAsync(JellyfinSettings settings, CancellationToken ct)
    {
        if (!settings.IsConfigured) return new(false, Message: "Enter the Jellyfin URL and an API key");
        try
        {
            var info = await GetAsync<SystemInfo>(settings, "System/Info", ct);
            return new(true, info?.ServerName, info?.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException)
        {
            return new(false, Message: ex.Message);
        }
    }

    /// <summary>
    /// Picks the Jellyfin item for <paramref name="lookup"/>: one with a matching TMDB, TheTVDB or IMDb id, otherwise one
    /// with the same name and (about) the same year whose own ids don't point at a different title.
    /// </summary>
    public static JellyfinItem? Match(IReadOnlyList<JellyfinItem> items, JellyfinLookup lookup)
    {
        var byId = items.FirstOrDefault(i => SameId(i, "Tmdb", lookup.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            || SameId(i, "Tvdb", lookup.TvdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            || SameId(i, "Imdb", lookup.ImdbId));
        if (byId is not null) return byId;

        return items.FirstOrDefault(i =>
            (SameName(i.Name, lookup.Title) || SameName(i.Name, lookup.OriginalTitle))
            && (lookup.Year == 0 || i.ProductionYear is null || Math.Abs(i.ProductionYear.Value - lookup.Year) <= 1)
            && !Conflicts(i, "Tmdb", lookup.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && !Conflicts(i, "Tvdb", lookup.TvdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture))
            && !Conflicts(i, "Imdb", lookup.ImdbId));
    }

    /// <summary>The item's details page in the Jellyfin web client.</summary>
    public static string ItemUrl(JellyfinSettings settings, JellyfinItem item)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(settings.PublicUrl) ? settings.Url! : settings.PublicUrl).Trim().TrimEnd('/');
        var server = string.IsNullOrEmpty(item.ServerId) ? "" : "&serverId=" + Uri.EscapeDataString(item.ServerId);
        return $"{baseUrl}/web/#/details?id={Uri.EscapeDataString(item.Id)}{server}";
    }

    private static string? ProviderId(JellyfinItem item, string provider) =>
        item.ProviderIds?.FirstOrDefault(p => string.Equals(p.Key, provider, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v ? v.Trim() : null;

    private static bool SameId(JellyfinItem item, string provider, string? ours) =>
        !string.IsNullOrEmpty(ours) && string.Equals(ProviderId(item, provider), ours, StringComparison.OrdinalIgnoreCase);

    private static bool Conflicts(JellyfinItem item, string provider, string? ours) =>
        !string.IsNullOrEmpty(ours) && ProviderId(item, provider) is { } theirs && !string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase);

    private static bool SameName(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task<T?> GetAsync<T>(JellyfinSettings settings, string relative, CancellationToken ct)
    {
        var url = new Uri(new Uri(settings.Url!.Trim().TrimEnd('/') + "/"), relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var key = settings.ApiKey!.Trim();
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{key}\"");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", key); // servers older than 10.9

        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("Jellyfin {Path} answered HTTP {Status}", url.AbsolutePath, (int)response.StatusCode);
            throw new HttpRequestException($"Jellyfin answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
        return await JsonSerializer.DeserializeAsync<T>(body, JsonSerializerOptions.Web, ct);
    }
}

public static class JellyfinEndpoints
{
    public static IServiceCollection AddKrakarrJellyfin(this IServiceCollection services)
    {
        services.AddHttpClient(JellyfinClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<JellyfinClient>();
        return services;
    }

    /// <summary>
    /// <c>GET /api/v1/movies/{id}/jellyfin</c> and <c>/api/v1/series/{id}/jellyfin</c>: the title's Jellyfin page, or 204
    /// when Jellyfin isn't configured, nothing is downloaded yet, or Jellyfin doesn't have it (yet). Readable by every user.
    /// </summary>
    public static IEndpointRouteBuilder MapJellyfinEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/movies/{id:int}/jellyfin", async (int id, KrakarrDbContext db, ISettingsService settings, JellyfinClient jellyfin, ILogger<JellyfinClient> logger, CancellationToken ct) =>
        {
            var config = await settings.GetAsync<JellyfinSettings>(ct);
            if (!config.IsConfigured) return Results.NoContent();
            var movie = await db.Movies.AsNoTracking().Where(m => m.Id == id)
                .Select(m => new { m.Title, m.OriginalTitle, m.Year, m.TmdbId, m.ImdbId, HasFile = m.MovieFileId != null })
                .FirstOrDefaultAsync(ct);
            if (movie is null) return Results.NotFound();
            if (!movie.HasFile) return Results.NoContent();
            return await Resolve(jellyfin, logger, config, new(JellyfinItemKind.Movie, movie.Title, movie.OriginalTitle, movie.Year, movie.TmdbId, null, movie.ImdbId), ct);
        }).WithTags("Jellyfin");

        app.MapGet("/api/v1/series/{id:int}/jellyfin", async (int id, KrakarrDbContext db, ISettingsService settings, JellyfinClient jellyfin, ILogger<JellyfinClient> logger, CancellationToken ct) =>
        {
            var config = await settings.GetAsync<JellyfinSettings>(ct);
            if (!config.IsConfigured) return Results.NoContent();
            var series = await db.Series.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new { s.Title, s.Year, s.TmdbId, s.TvdbId, s.ImdbId, HasFile = db.EpisodeFiles.Any(f => f.SeriesId == s.Id) })
                .FirstOrDefaultAsync(ct);
            if (series is null) return Results.NotFound();
            if (!series.HasFile) return Results.NoContent();
            return await Resolve(jellyfin, logger, config, new(JellyfinItemKind.Series, series.Title, null, series.Year, series.TmdbId, series.TvdbId, series.ImdbId), ct);
        }).WithTags("Jellyfin");

        // Under /api/v1/settings, so admins only (RoleAccess).
        app.MapPost("/api/v1/settings/jellyfin/test", async (JellyfinSettings input, ISettingsService settings, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            if (input.ApiKey == JellyfinSettings.ApiKeyMask) input = input with { ApiKey = (await settings.GetAsync<JellyfinSettings>(ct)).ApiKey };
            return await jellyfin.TestAsync(input, ct);
        }).WithTags("Settings");

        return app;
    }

    private static async Task<IResult> Resolve(JellyfinClient jellyfin, ILogger logger, JellyfinSettings config, JellyfinLookup lookup, CancellationToken ct)
    {
        try
        {
            return await jellyfin.FindItemUrlAsync(config, lookup, ct) is { } url ? Results.Ok(new { Url = url }) : Results.NoContent();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The link is a convenience: an unreachable Jellyfin just hides it.
            logger.LogWarning("Cannot look up {Title} on Jellyfin: {Error}", lookup.Title, ex.Message);
            return Results.NoContent();
        }
    }
}
