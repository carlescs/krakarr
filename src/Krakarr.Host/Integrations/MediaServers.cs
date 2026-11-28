using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Krakarr.Core.Configuration;
using Krakarr.Core.Http;
using Krakarr.Core.MediaServers;
using Krakarr.Data;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Host.Integrations;

public sealed record MediaServerTestResult(bool Success, string? ServerName = null, string? Version = null, string? Message = null);

/// <summary>Folders to rescan, or <see cref="Everything"/> when too many folders changed to list them.</summary>
public sealed record MediaServerRefreshBatch(IReadOnlyList<string> Folders, bool Everything);

/// <summary>Path helpers shared by the queue and the media server clients.</summary>
public static class MediaServerPaths
{
    /// <summary>Trims whitespace and trailing separators, keeping a bare root ("/") as it is.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        var withoutSlash = trimmed.TrimEnd('/', '\\');
        return withoutSlash.Length == 0 ? trimmed[..1] : withoutSlash;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder) =>
        path.StartsWith(folder, StringComparison.Ordinal)
        && (path.Length == folder.Length || folder[^1] is '/' or '\\' || path[folder.Length] is '/' or '\\');

    /// <summary>
    /// Translates a path Krakarr sees into the path the media server sees, using the mapping with the longest matching
    /// <see cref="PathMapping.From"/>. A target with backslashes only (a Windows server) gets backslashes throughout.
    /// </summary>
    public static string Map(string path, IEnumerable<PathMapping>? mappings)
    {
        var best = (mappings ?? [])
            .Select(m => (From: Normalize(m.From), To: Normalize(m.To)))
            .Where(m => m.From is not null && m.To is not null && IsSameOrUnder(path, m.From))
            .OrderByDescending(m => m.From!.Length)
            .FirstOrDefault();
        if (best.From is null) return path;

        var rest = path[best.From.Length..].TrimStart('/', '\\');
        var to = best.To!;
        var separator = to.Contains('\\') && !to.Contains('/') ? '\\' : '/';
        if (rest.Length == 0) return to;
        if (separator == '\\') rest = rest.Replace('/', '\\');
        return to[^1] is '/' or '\\' ? to + rest : to + separator + rest;
    }
}

/// <summary>
/// Collects library folders that changed and hands them out once they have been quiet for a while, so a season pack
/// (or a bulk delete) triggers one rescan per folder instead of one per file. Bounded: past <see cref="Capacity"/>
/// folders it stops listing them and asks for a full library refresh instead.
/// </summary>
public sealed class MediaServerRefreshQueue(TimeProvider time) : IMediaServerNotifier
{
    public const int Capacity = 200;
    /// <summary>A folder is refreshed once nothing in it changed for this long...</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);
    /// <summary>...or at the latest this long after its first change, so a steady stream of changes still gets through.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset First, DateTimeOffset Last)> _pending = new(StringComparer.Ordinal);
    private (DateTimeOffset First, DateTimeOffset Last)? _everything;

    public void PathChanged(string path)
    {
        if (MediaServerPaths.Normalize(path) is not { } folder) return;
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_everything is { } all)
            {
                _everything = (all.First, now);
                return;
            }

            // Already covered by a pending folder (the same one or a parent): push its deadline back.
            string? covering = null;
            foreach (var key in _pending.Keys)
            {
                if (MediaServerPaths.IsSameOrUnder(folder, key)) { covering = key; break; }
            }
            if (covering is not null)
            {
                _pending[covering] = (_pending[covering].First, now);
                return;
            }

            // Pending subfolders are covered by the new folder.
            var first = now;
            foreach (var key in _pending.Keys.Where(k => MediaServerPaths.IsSameOrUnder(k, folder)).ToList())
            {
                if (_pending[key].First < first) first = _pending[key].First;
                _pending.Remove(key);
            }

            if (_pending.Count >= Capacity)
            {
                first = _pending.Values.Aggregate(first, (min, e) => e.First < min ? e.First : min);
                _pending.Clear();
                _everything = (first, now);
                return;
            }

            _pending[folder] = (first, now);
        }
    }

    /// <summary>The folders whose wait is over (removed from the queue), or null when none is due.</summary>
    public MediaServerRefreshBatch? TakeDue()
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_everything is { } all)
            {
                if (!IsDue(all, now)) return null;
                _everything = null;
                return new([], true);
            }

            List<string>? due = null;
            foreach (var (folder, times) in _pending)
            {
                if (IsDue(times, now)) (due ??= []).Add(folder);
            }
            if (due is null) return null;
            foreach (var folder in due) _pending.Remove(folder);
            return new(due, false);
        }
    }

    public int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    private static bool IsDue((DateTimeOffset First, DateTimeOffset Last) times, DateTimeOffset now) =>
        now - times.Last >= Quiet || now - times.First >= MaxWait;
}

public enum MediaBrowserKind
{
    Jellyfin,
    Emby,
}

/// <summary>Library refreshes and connection tests for Jellyfin and Emby, which share this part of their API.</summary>
public sealed class MediaBrowserClient(IHttpClientFactory httpClientFactory, ILogger<MediaBrowserClient> logger)
{
    public const string EmbyHttpClientName = "emby";

    private const long MaxResponseBytes = 256 * 1024;

    private static readonly JsonSerializerOptions PascalCase = new();

    private sealed record SystemInfo(string? ServerName, string? Version);

    /// <summary>
    /// Reports the changed folders (<c>POST /Library/Media/Updated</c>) so the server rescans just those; when the server
    /// rejects that, or the batch asks for everything, refreshes the whole library (<c>POST /Library/Refresh</c>).
    /// </summary>
    public async Task RefreshAsync(MediaBrowserKind kind, string url, string apiKey, IReadOnlyList<PathMapping> mappings, MediaServerRefreshBatch batch, CancellationToken ct)
    {
        if (!batch.Everything)
        {
            var updates = new { Updates = batch.Folders.Select(f => new { Path = MediaServerPaths.Map(f, mappings), UpdateType = "Modified" }).ToList() };
            using (var response = await SendAsync(kind, url, apiKey, HttpMethod.Post, "Library/Media/Updated", JsonContent.Create(updates, options: PascalCase), ct))
            {
                if (response.IsSuccessStatusCode) return;
                logger.LogInformation("{Server} answered HTTP {Status} to a folder refresh; refreshing the whole library", kind, (int)response.StatusCode);
            }
        }

        using var full = await SendAsync(kind, url, apiKey, HttpMethod.Post, "Library/Refresh", null, ct);
        if (!full.IsSuccessStatusCode) throw new HttpRequestException($"{kind} answered HTTP {(int)full.StatusCode}", null, full.StatusCode);
    }

    public async Task<MediaServerTestResult> TestAsync(MediaBrowserKind kind, string? url, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey)) return new(false, Message: $"Enter the {kind} URL and an API key");
        try
        {
            using var response = await SendAsync(kind, url, apiKey, HttpMethod.Get, "System/Info", null, ct);
            if (!response.IsSuccessStatusCode) return new(false, Message: $"{kind} answered HTTP {(int)response.StatusCode}");
            await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
            var info = await JsonSerializer.DeserializeAsync<SystemInfo>(body, JsonSerializerOptions.Web, ct);
            return new(true, info?.ServerName, info?.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return new(false, Message: ex.Message);
        }
    }

    private Task<HttpResponseMessage> SendAsync(MediaBrowserKind kind, string url, string apiKey, HttpMethod method, string relative, HttpContent? content, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(url.Trim().TrimEnd('/') + "/"), relative)) { Content = content };
        var key = apiKey.Trim();
        if (kind == MediaBrowserKind.Jellyfin) request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", $"Token=\"{key}\"");
        request.Headers.TryAddWithoutValidation("X-Emby-Token", key);
        var http = httpClientFactory.CreateClient(kind == MediaBrowserKind.Jellyfin ? JellyfinClient.HttpClientName : EmbyHttpClientName);
        return SendAndDisposeAsync(http, request, ct);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using (request) return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}

/// <summary>A Plex library section and the folders it covers; <see cref="Type"/> is "movie" or "show" for video libraries.</summary>
public sealed record PlexSection(string Key, string? Title, IReadOnlyList<string> Locations, string? Type = null);

/// <summary>Refreshes Plex library sections by path, and tests the connection.</summary>
public sealed class PlexClient(IHttpClientFactory httpClientFactory, ILogger<PlexClient> logger)
{
    public const string HttpClientName = "plex";

    /// <summary>The section list is a few hundred bytes per library.</summary>
    private const long MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// A show's episode list carries media and part details (a couple of KB per episode). It is read as a stream, keeping
    /// only a few attributes per item, so the bound only caps the transfer.
    /// </summary>
    private const long MaxItemsBytes = 64 * 1024 * 1024;

    /// <summary>Most items a single item list may hold (a show's episodes, or the titles matching a search).</summary>
    private const int MaxItems = 20_000;

    /// <summary>
    /// The movie or show for <paramref name="lookup"/>, with the token owner's play state (a show's is empty), or null when
    /// no Plex library has it. Searches the movie or show libraries by title, then matches like Jellyfin (ids, then name and year).
    /// </summary>
    public async Task<JellyfinItem?> FindItemAsync(PlexSettings settings, JellyfinLookup lookup, CancellationToken ct)
    {
        var (sectionType, itemType, element) = lookup.Kind == JellyfinItemKind.Movie ? ("movie", 1, "Video") : ("show", 2, "Directory");
        var sections = Sections(await GetXmlAsync(settings, "library/sections", ct)).Where(s => s.Type == sectionType).ToList();
        foreach (var term in new[] { lookup.Title, lookup.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var section in sections)
            {
                var relative = $"library/sections/{Uri.EscapeDataString(section.Key)}/all?type={itemType}&includeGuids=1&title={Uri.EscapeDataString(term!)}";
                if (JellyfinClient.Match(await GetItemsAsync(settings, relative, element, ct), lookup) is { } item) return item;
            }
        }
        return null;
    }

    /// <summary>Every episode of a show (by its <c>ratingKey</c>), with ids and the token owner's play state.</summary>
    public Task<List<JellyfinItem>> EpisodesAsync(PlexSettings settings, string showKey, CancellationToken ct) =>
        GetItemsAsync(settings, $"library/metadata/{Uri.EscapeDataString(showKey)}/allLeaves?includeGuids=1", "Video", ct);

    /// <summary>Reads the <paramref name="element"/> items of a Plex list as a stream, mapped onto <see cref="JellyfinItem"/>.</summary>
    private async Task<List<JellyfinItem>> GetItemsAsync(PlexSettings settings, string relative, string element, CancellationToken ct)
    {
        using var request = Request(settings, relative);
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Plex answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
        await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxItemsBytes, ct);
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
        return await ReadItemsAsync(reader, element);
    }

    /// <summary>
    /// Maps Plex items (<c>&lt;Video&gt;</c> or <c>&lt;Directory&gt;</c> directly under the container) onto <see cref="JellyfinItem"/>:
    /// <c>&lt;Guid id="tmdb://1"/&gt;</c> children become provider ids, <c>viewCount</c>/<c>viewOffset</c>/<c>lastViewedAt</c> the play state.
    /// </summary>
    public static async Task<List<JellyfinItem>> ReadItemsAsync(XmlReader reader, string element)
    {
        var items = new List<JellyfinItem>();
        while (await reader.ReadAsync())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.Name != element) continue;
            var key = reader.GetAttribute("ratingKey");
            var title = reader.GetAttribute("title");
            var year = Int(reader.GetAttribute("year"));
            var season = Int(reader.GetAttribute("parentIndex"));
            var index = Int(reader.GetAttribute("index"));
            var duration = Long(reader.GetAttribute("duration"));
            var viewCount = Int(reader.GetAttribute("viewCount")) ?? 0;
            var viewOffset = Long(reader.GetAttribute("viewOffset")) ?? 0;
            var lastViewed = Long(reader.GetAttribute("lastViewedAt")) is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : (DateTimeOffset?)null;
            var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (!reader.IsEmptyElement)
            {
                var depth = reader.Depth;
                while (await reader.ReadAsync() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth + 1 && reader.Name == "Guid"
                        && reader.GetAttribute("id") is { } guid && guid.IndexOf("://", StringComparison.Ordinal) is var sep and > 0)
                        ids.TryAdd(guid[..sep], guid[(sep + 3)..]);
                }
            }
            if (string.IsNullOrEmpty(key)) continue;
            // Plex counts in milliseconds, Jellyfin in ticks of 100 ns.
            items.Add(new(key, title, null, year, ids, season, index, null, duration * 10_000,
                new JellyfinUserData(viewCount > 0, viewCount, viewOffset * 10_000, lastViewed)));
            if (items.Count >= MaxItems) break;
        }
        return items;
    }

    private static int? Int(string? s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? Long(string? s) => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    public async Task<MediaServerTestResult> TestAsync(PlexSettings settings, CancellationToken ct)
    {
        if (!settings.IsConfigured) return new(false, Message: "Enter the Plex URL and a token");
        try
        {
            var root = await GetXmlAsync(settings, "", ct);
            var sections = Sections(await GetXmlAsync(settings, "library/sections", ct));
            var version = root.Root?.Attribute("version")?.Value;
            return new(true, root.Root?.Attribute("friendlyName")?.Value, version,
                sections.Count == 0 ? "Connected, but Plex has no libraries yet" : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return new(false, Message: ex.Message);
        }
    }

    /// <summary>
    /// Refreshes, for each folder, the section whose location contains it (<c>/library/sections/{id}/refresh?path=</c>),
    /// or every section when the batch asks for everything.
    /// </summary>
    public async Task RefreshAsync(PlexSettings settings, MediaServerRefreshBatch batch, CancellationToken ct)
    {
        var sections = Sections(await GetXmlAsync(settings, "library/sections", ct));
        if (batch.Everything)
        {
            foreach (var section in sections) await RefreshSectionAsync(settings, section, null, ct);
            return;
        }

        foreach (var folder in batch.Folders)
        {
            var path = MediaServerPaths.Map(folder, settings.PathMappings);
            if (Match(sections, path) is { } section) await RefreshSectionAsync(settings, section, path, ct);
            else logger.LogWarning("No Plex library contains {Path}; add a path mapping if Plex sees the library under another path", path);
        }
    }

    /// <summary>The section with the longest location that contains <paramref name="path"/>.</summary>
    public static PlexSection? Match(IEnumerable<PlexSection> sections, string path) =>
        sections
            .SelectMany(s => s.Locations.Select(l => (Section: s, Location: MediaServerPaths.Normalize(l))))
            .Where(x => x.Location is not null && MediaServerPaths.IsSameOrUnder(path, x.Location))
            .OrderByDescending(x => x.Location!.Length)
            .Select(x => x.Section)
            .FirstOrDefault();

    public static List<PlexSection> Sections(XDocument doc) =>
        doc.Root?.Elements("Directory")
            .Where(d => !string.IsNullOrEmpty(d.Attribute("key")?.Value))
            .Select(d => new PlexSection(d.Attribute("key")!.Value, d.Attribute("title")?.Value,
                d.Elements("Location").Select(l => l.Attribute("path")?.Value).OfType<string>().ToList(), d.Attribute("type")?.Value))
            .ToList() ?? [];

    private async Task RefreshSectionAsync(PlexSettings settings, PlexSection section, string? path, CancellationToken ct)
    {
        var relative = $"library/sections/{Uri.EscapeDataString(section.Key)}/refresh" + (path is null ? "" : "?path=" + Uri.EscapeDataString(path));
        using var request = Request(settings, relative);
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Plex answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
    }

    private async Task<XDocument> GetXmlAsync(PlexSettings settings, string relative, CancellationToken ct)
    {
        using var request = Request(settings, relative);
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Plex answered HTTP {(int)response.StatusCode}", null, response.StatusCode);
        await using var body = await response.Content.ReadAsBoundedStreamAsync(MaxResponseBytes, ct);
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
    }

    private static HttpRequestMessage Request(PlexSettings settings, string relative)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(settings.Url!.Trim().TrimEnd('/') + "/"), relative));
        request.Headers.TryAddWithoutValidation("X-Plex-Token", settings.Token!.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        return request;
    }
}

/// <summary>Sends the queued folder refreshes to every configured media server.</summary>
public sealed class MediaServerRefresher(
    MediaServerRefreshQueue queue,
    IServiceScopeFactory scopes,
    MediaBrowserClient mediaBrowser,
    PlexClient plex,
    TimeProvider time,
    ILogger<MediaServerRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Poll, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (queue.TakeDue() is { } batch) await RefreshAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task RefreshAsync(MediaServerRefreshBatch batch, CancellationToken ct)
    {
        JellyfinSettings jellyfin;
        EmbySettings emby;
        PlexSettings plexSettings;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            jellyfin = await settings.GetAsync<JellyfinSettings>(ct);
            emby = await settings.GetAsync<EmbySettings>(ct);
            plexSettings = await settings.GetAsync<PlexSettings>(ct);
        }

        if (jellyfin.IsConfigured && jellyfin.RefreshLibrary)
            await TryAsync("Jellyfin", batch, () => mediaBrowser.RefreshAsync(MediaBrowserKind.Jellyfin, jellyfin.Url!, jellyfin.ApiKey!, jellyfin.PathMappings, batch, ct), ct);
        if (emby.IsConfigured)
            await TryAsync("Emby", batch, () => mediaBrowser.RefreshAsync(MediaBrowserKind.Emby, emby.Url!, emby.ApiKey!, emby.PathMappings, batch, ct), ct);
        if (plexSettings.IsConfigured)
            await TryAsync("Plex", batch, () => plex.RefreshAsync(plexSettings, batch, ct), ct);
    }

    private async Task TryAsync(string server, MediaServerRefreshBatch batch, Func<Task> refresh, CancellationToken ct)
    {
        try
        {
            await refresh();
            logger.LogDebug("Asked {Server} to refresh {Folders}", server, batch.Everything ? "the whole library" : string.Join(", ", batch.Folders));
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or UriFormatException or InvalidOperationException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The server still picks the change up with its own scheduled scan.
            logger.LogWarning("Could not ask {Server} to refresh its library: {Error}", server, ex.Message);
        }
    }
}

public static class MediaServerEndpoints
{
    public static IServiceCollection AddKrakarrMediaServers(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(MediaBrowserClient.EmbyHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(PlexClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<MediaBrowserClient>();
        services.AddSingleton<PlexClient>();
        services.AddSingleton<MediaServerRefreshQueue>();
        services.Replace(ServiceDescriptor.Singleton<IMediaServerNotifier>(sp => sp.GetRequiredService<MediaServerRefreshQueue>()));
        services.AddSingleton<MediaServerRefresher>();
        services.AddHostedService(sp => sp.GetRequiredService<MediaServerRefresher>());
        services.AddScoped<PlayStatusService>();
        return services;
    }

    /// <summary>
    /// Play status of library titles (readable by every user: GETs under movies/series), plus connection tests and user
    /// lists for the settings page (under <c>/api/v1/settings</c>, so admins only; RoleAccess).
    /// </summary>
    public static IEndpointRouteBuilder MapMediaServerEndpoints(this IEndpointRouteBuilder app)
    {
        // The movie's watched state on each media server set up to report it; empty when none is or nothing is downloaded.
        app.MapGet("/api/v1/movies/{id:int}/playstatus", async (int id, PlayStatusService playStatus, CancellationToken ct) =>
            await playStatus.MovieAsync(id, ct) is { } states ? Results.Ok(states) : Results.NotFound()).WithTags("Media servers");

        // The series' watched episodes on each media server set up to report it.
        app.MapGet("/api/v1/series/{id:int}/playstatus", async (int id, PlayStatusService playStatus, CancellationToken ct) =>
            await playStatus.SeriesAsync(id, ct) is { } states ? Results.Ok(states) : Results.NotFound()).WithTags("Media servers");

        app.MapPost("/api/v1/settings/jellyfin/users", async (JellyfinSettings input, ISettingsService settings, JellyfinClient client, CancellationToken ct) =>
        {
            if (input.ApiKey == JellyfinSettings.ApiKeyMask) input = input with { ApiKey = (await settings.GetAsync<JellyfinSettings>(ct)).ApiKey };
            return await UsersAsync(client, MediaBrowserServer.From(input), "Jellyfin", ct);
        }).WithTags("Settings");

        app.MapPost("/api/v1/settings/emby/users", async (EmbySettings input, ISettingsService settings, JellyfinClient client, CancellationToken ct) =>
        {
            if (input.ApiKey == EmbySettings.ApiKeyMask) input = input with { ApiKey = (await settings.GetAsync<EmbySettings>(ct)).ApiKey };
            return await UsersAsync(client, MediaBrowserServer.From(input), "Emby", ct);
        }).WithTags("Settings");

        app.MapPost("/api/v1/settings/emby/test", async (EmbySettings input, ISettingsService settings, MediaBrowserClient client, CancellationToken ct) =>
        {
            var key = input.ApiKey == EmbySettings.ApiKeyMask ? (await settings.GetAsync<EmbySettings>(ct)).ApiKey : input.ApiKey;
            return await client.TestAsync(MediaBrowserKind.Emby, input.Url, key, ct);
        }).WithTags("Settings");

        app.MapPost("/api/v1/settings/plex/test", async (PlexSettings input, ISettingsService settings, PlexClient client, CancellationToken ct) =>
        {
            if (input.Token == PlexSettings.TokenMask) input = input with { Token = (await settings.GetAsync<PlexSettings>(ct)).Token };
            return await client.TestAsync(input, ct);
        }).WithTags("Settings");

        return app;
    }

    private static async Task<IResult> UsersAsync(JellyfinClient client, MediaBrowserServer? server, string name, CancellationToken ct)
    {
        if (server is null) return Results.Problem($"Enter the {name} URL and an API key", statusCode: StatusCodes.Status400BadRequest);
        try
        {
            return Results.Ok(await client.UsersAsync(server, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return Results.Problem($"Cannot list the {name} users: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
