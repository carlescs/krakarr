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

/// <summary>An episode as Krakarr knows it, used to find it under the series' item on the Jellyfin server.</summary>
public sealed record JellyfinEpisodeLookup(int SeasonNumber, int EpisodeNumber, int? TmdbId, int? TvdbId);

/// <summary>
/// A Jellyfin (or Emby, or Plex mapped onto it) item; the index numbers are only set for episodes (season, episode and the
/// last episode of a multi-episode file). <see cref="UserData"/> is only asked for when reading play status.
/// </summary>
public sealed record JellyfinItem(string Id, string? Name, string? ServerId, int? ProductionYear, Dictionary<string, string?>? ProviderIds,
    int? ParentIndexNumber = null, int? IndexNumber = null, int? IndexNumberEnd = null, long? RunTimeTicks = null, JellyfinUserData? UserData = null);

/// <summary>One user's play state of an item (Jellyfin, Emby; Plex is mapped onto it).</summary>
public sealed record JellyfinUserData(bool Played, int PlayCount, long PlaybackPositionTicks, DateTimeOffset? LastPlayedDate);

/// <summary>A user account on a Jellyfin or Emby server.</summary>
public sealed record MediaServerUser(string Id, string? Name);

/// <summary>A Jellyfin or Emby server to talk to.</summary>
public sealed record MediaBrowserServer(MediaBrowserKind Kind, string Url, string ApiKey)
{
    public static MediaBrowserServer? From(JellyfinSettings s) => s.IsConfigured ? new(MediaBrowserKind.Jellyfin, s.Url!, s.ApiKey!) : null;
    public static MediaBrowserServer? From(EmbySettings s) => s.IsConfigured ? new(MediaBrowserKind.Emby, s.Url!, s.ApiKey!) : null;
}

public sealed record JellyfinTestResult(bool Success, string? ServerName = null, string? Version = null, string? Message = null);

/// <summary>
/// Finds library titles on a Jellyfin (or Emby, which shares this part of the API) server, builds links to their pages in
/// the Jellyfin web client and reads a user's play state.
/// </summary>
public sealed class JellyfinClient(IHttpClientFactory httpClientFactory, ILogger<JellyfinClient> logger)
{
    public const string HttpClientName = "jellyfin";

    /// <summary>Search results are capped at <see cref="SearchLimit"/> slim items; this bound is generous for that.</summary>
    private const long MaxResponseBytes = 4 * 1024 * 1024;

    private const int SearchLimit = 25;

    /// <summary>A whole series' episodes with ids and user data: about 1 KB each, so room for several thousand.</summary>
    private const long MaxEpisodesBytes = 16 * 1024 * 1024;

    private sealed record ItemsResponse(List<JellyfinItem>? Items);

    private sealed record SystemInfo(string? ServerName, string? Version);

    /// <summary>Link to the title's page in Jellyfin, or null when Jellyfin is not configured or doesn't have it.</summary>
    public async Task<string?> FindItemUrlAsync(JellyfinSettings settings, JellyfinLookup lookup, CancellationToken ct) =>
        MediaBrowserServer.From(settings) is { } server && await FindItemAsync(server, lookup, null, ct) is { } item ? ItemUrl(settings, item) : null;

    /// <summary>Link to the episode's page in Jellyfin, or null when Jellyfin is not configured or doesn't have the series or the episode.</summary>
    public async Task<string?> FindEpisodeUrlAsync(JellyfinSettings settings, JellyfinLookup series, JellyfinEpisodeLookup episode, CancellationToken ct)
    {
        if (MediaBrowserServer.From(settings) is not { } server || await FindItemAsync(server, series, null, ct) is not { } show) return null;
        // One season's episodes: bounded, and the only way to match a multi-episode file by its index range.
        var query = $"Shows/{Uri.EscapeDataString(show.Id)}/Episodes?Season={episode.SeasonNumber}"
            + "&Fields=ProviderIds&EnableImages=false&EnableUserData=false";
        var response = await GetAsync<ItemsResponse>(server, query, MaxResponseBytes, ct);
        return MatchEpisode(response?.Items ?? [], episode) is { } item ? ItemUrl(settings, item) : null;
    }

    /// <summary>The movie or series as <paramref name="userId"/> sees it (with its play state), or null when the server doesn't have it.</summary>
    public Task<JellyfinItem?> FindUserItemAsync(MediaBrowserServer server, JellyfinLookup lookup, string userId, CancellationToken ct) =>
        FindItemAsync(server, lookup, userId, ct);

    /// <summary>Every episode of a series item, with ids and <paramref name="userId"/>'s play state.</summary>
    public async Task<IReadOnlyList<JellyfinItem>> EpisodesAsync(MediaBrowserServer server, string seriesItemId, string userId, CancellationToken ct)
    {
        var query = $"Shows/{Uri.EscapeDataString(seriesItemId)}/Episodes?UserId={Uri.EscapeDataString(userId)}"
            + "&Fields=ProviderIds&EnableImages=false&EnableUserData=true";
        return (await GetAsync<ItemsResponse>(server, query, MaxEpisodesBytes, ct))?.Items ?? [];
    }

    /// <summary>The server's user accounts, to pick whose play state Krakarr shows.</summary>
    public async Task<IReadOnlyList<MediaServerUser>> UsersAsync(MediaBrowserServer server, CancellationToken ct) =>
        (await GetAsync<List<MediaServerUser>>(server, "Users", MaxResponseBytes, ct) ?? [])
            .Where(u => !string.IsNullOrEmpty(u.Id))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private async Task<JellyfinItem?> FindItemAsync(MediaBrowserServer server, JellyfinLookup lookup, string? userId, CancellationToken ct)
    {
        var user = userId is null ? "&EnableUserData=false" : $"&UserId={Uri.EscapeDataString(userId)}&EnableUserData=true";
        foreach (var term in new[] { lookup.Title, lookup.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var query = $"Items?Recursive=true&IncludeItemTypes={lookup.Kind}&SearchTerm={Uri.EscapeDataString(term!)}"
                + $"&Fields=ProviderIds&EnableImages=false{user}&Limit={SearchLimit}";
            var response = await GetAsync<ItemsResponse>(server, query, MaxResponseBytes, ct);
            if (Match(response?.Items ?? [], lookup) is { } item) return item;
        }

        return null;
    }

    public async Task<JellyfinTestResult> TestAsync(JellyfinSettings settings, CancellationToken ct)
    {
        if (!settings.IsConfigured) return new(false, Message: "Enter the Jellyfin URL and an API key");
        try
        {
            var info = await GetAsync<SystemInfo>(MediaBrowserServer.From(settings)!, "System/Info", MaxResponseBytes, ct);
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

    /// <summary>
    /// Picks the Jellyfin episode for <paramref name="episode"/> among a season's episodes: one with a matching TheTVDB or
    /// TMDB id, otherwise the one numbered like it (a multi-episode file covers every number up to its end index).
    /// </summary>
    public static JellyfinItem? MatchEpisode(IReadOnlyList<JellyfinItem> items, JellyfinEpisodeLookup episode)
    {
        var tvdb = episode.TvdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tmdb = episode.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var byId = items.FirstOrDefault(i => SameId(i, "Tvdb", tvdb) || SameId(i, "Tmdb", tmdb));
        if (byId is not null) return byId;

        return items.FirstOrDefault(i =>
            (i.ParentIndexNumber is null || i.ParentIndexNumber == episode.SeasonNumber)
            && i.IndexNumber is { } first
            && (first == episode.EpisodeNumber || (i.IndexNumberEnd is { } last && first <= episode.EpisodeNumber && episode.EpisodeNumber <= last)));
    }

    /// <summary>
    /// <see cref="MatchEpisode"/> for many episodes at once (a whole series): indexes the items once instead of scanning
    /// them for every episode. The result lines up with <paramref name="episodes"/>.
    /// </summary>
    public static JellyfinItem?[] MatchEpisodes(IReadOnlyList<JellyfinItem> items, IReadOnlyList<JellyfinEpisodeLookup> episodes)
    {
        var byTvdb = new Dictionary<string, JellyfinItem>(StringComparer.OrdinalIgnoreCase);
        var byTmdb = new Dictionary<string, JellyfinItem>(StringComparer.OrdinalIgnoreCase);
        var byNumber = new Dictionary<(int Season, int Episode), JellyfinItem>();
        foreach (var item in items)
        {
            if (ProviderId(item, "Tvdb") is { } tvdb) byTvdb.TryAdd(tvdb, item);
            if (ProviderId(item, "Tmdb") is { } tmdb) byTmdb.TryAdd(tmdb, item);
            if (item is { ParentIndexNumber: { } season, IndexNumber: { } first })
            {
                // A multi-episode file covers its whole range (capped, in case the server reports nonsense).
                var last = item.IndexNumberEnd is { } end && end >= first ? Math.Min(end, first + 50) : first;
                for (var n = first; n <= last; n++) byNumber.TryAdd((season, n), item);
            }
        }

        var result = new JellyfinItem?[episodes.Count];
        for (var i = 0; i < episodes.Count; i++)
        {
            var e = episodes[i];
            result[i] = (e.TvdbId is { } tvdb && byTvdb.TryGetValue(tvdb.ToString(System.Globalization.CultureInfo.InvariantCulture), out var a) ? a : null)
                ?? (e.TmdbId is { } tmdb && byTmdb.TryGetValue(tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture), out var b) ? b : null)
                ?? (byNumber.TryGetValue((e.SeasonNumber, e.EpisodeNumber), out var c) ? c : null);
        }
        return result;
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

    private async Task<T?> GetAsync<T>(MediaBrowserServer server, string relative, long maxBytes, CancellationToken ct)
    {
        var url = new Uri(new Uri(server.Url.Trim().TrimEnd('/') + "/"), relative);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var key = server.ApiKey.Trim();
        if (server.Kind == MediaBrowserKind.Jellyfin) request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{key}\"");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", key); // Emby, and Jellyfin servers older than 10.9

        var http = httpClientFactory.CreateClient(server.Kind == MediaBrowserKind.Jellyfin ? HttpClientName : MediaBrowserClient.EmbyHttpClientName);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogDebug("{Server} {Path} answered HTTP {Status}", server.Kind, url.AbsolutePath, (int)response.StatusCode);
            throw new HttpRequestException($"{server.Kind} answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        await using var body = await response.Content.ReadAsBoundedStreamAsync(maxBytes, ct);
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
    /// <c>GET /api/v1/movies/{id}/jellyfin</c>, <c>/api/v1/series/{id}/jellyfin</c> and
    /// <c>/api/v1/series/{id}/episodes/{episodeId}/jellyfin</c>: the title's (or episode's) Jellyfin page, or 204
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
                .Select(s => new { Title = s.MetadataTitle != "" ? s.MetadataTitle : s.Title, s.Year, s.TmdbId, s.TvdbId, s.ImdbId, HasFile = db.EpisodeFiles.Any(f => f.SeriesId == s.Id) })
                .FirstOrDefaultAsync(ct);
            if (series is null) return Results.NotFound();
            if (!series.HasFile) return Results.NoContent();
            return await Resolve(jellyfin, logger, config, new(JellyfinItemKind.Series, series.Title, null, series.Year, series.TmdbId, series.TvdbId, series.ImdbId), ct);
        }).WithTags("Jellyfin");

        app.MapGet("/api/v1/series/{id:int}/episodes/{episodeId:int}/jellyfin", async (int id, int episodeId, KrakarrDbContext db, ISettingsService settings, JellyfinClient jellyfin, ILogger<JellyfinClient> logger, CancellationToken ct) =>
        {
            var config = await settings.GetAsync<JellyfinSettings>(ct);
            if (!config.IsConfigured) return Results.NoContent();
            var episode = await db.Episodes.AsNoTracking().Where(e => e.Id == episodeId && e.SeriesId == id)
                .Select(e => new { e.SeasonNumber, e.EpisodeNumber, e.TmdbId, e.TvdbId, HasFile = e.EpisodeFileId != null })
                .FirstOrDefaultAsync(ct);
            if (episode is null) return Results.NotFound();
            if (!episode.HasFile) return Results.NoContent();
            var series = await db.Series.AsNoTracking().Where(s => s.Id == id)
                .Select(s => new { Title = s.MetadataTitle != "" ? s.MetadataTitle : s.Title, s.Year, s.TmdbId, s.TvdbId, s.ImdbId })
                .FirstAsync(ct);
            var lookup = new JellyfinLookup(JellyfinItemKind.Series, series.Title, null, series.Year, series.TmdbId, series.TvdbId, series.ImdbId);
            return await Resolve(lookup.Title, logger, () => jellyfin.FindEpisodeUrlAsync(config, lookup,
                new(episode.SeasonNumber, episode.EpisodeNumber, episode.TmdbId, episode.TvdbId), ct), ct);
        }).WithTags("Jellyfin");

        // Under /api/v1/settings, so admins only (RoleAccess).
        app.MapPost("/api/v1/settings/jellyfin/test", async (JellyfinSettings input, ISettingsService settings, JellyfinClient jellyfin, CancellationToken ct) =>
        {
            if (input.ApiKey == JellyfinSettings.ApiKeyMask) input = input with { ApiKey = (await settings.GetAsync<JellyfinSettings>(ct)).ApiKey };
            return await jellyfin.TestAsync(input, ct);
        }).WithTags("Settings");

        return app;
    }

    private static Task<IResult> Resolve(JellyfinClient jellyfin, ILogger logger, JellyfinSettings config, JellyfinLookup lookup, CancellationToken ct) =>
        Resolve(lookup.Title, logger, () => jellyfin.FindItemUrlAsync(config, lookup, ct), ct);

    private static async Task<IResult> Resolve(string title, ILogger logger, Func<Task<string?>> find, CancellationToken ct)
    {
        try
        {
            return await find() is { } url ? Results.Ok(new { Url = url }) : Results.NoContent();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The link is a convenience: an unreachable Jellyfin just hides it.
            logger.LogWarning("Cannot look up {Title} on Jellyfin: {Error}", title, ex.Message);
            return Results.NoContent();
        }
    }
}
