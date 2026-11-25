using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.MediaServers;
using Krakarr.Host.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Host.Tests;

/// <summary>Records every request and answers with <see cref="Respond"/> (404 when it returns null).</summary>
public sealed class FakeMediaServer : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, string PathAndQuery, string? Body, IReadOnlyDictionary<string, string> Headers);

    public List<Call> Calls { get; } = [];

    public Func<Call, HttpResponseMessage?> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var call = new Call(request.Method, request.RequestUri!.PathAndQuery, body, headers);
        lock (Calls) Calls.Add(call);
        return Respond(call) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class MediaServerPathTests
{
    [Theory]
    [InlineData("/data/movies/Dune (2021)", "/data/movies/Dune (2021)")]
    [InlineData("/data/tv/Show/Season 01", "/media/tv/Show/Season 01")]
    [InlineData("/data/tv", "/media/tv")]
    [InlineData("/data/tv-other/Show", "/data/tv-other/Show")] // not under /data/tv
    [InlineData("/data/anime/Show", "D:\\Anime\\Show")]
    [InlineData("/data/anime/Show/Season 01", "D:\\Anime\\Show\\Season 01")]
    [InlineData("/data/tv/kids/Bluey", "/kids/Bluey")] // the longest mapping wins
    public void Maps_krakarr_paths_to_server_paths(string path, string expected)
    {
        PathMapping[] mappings =
        [
            new() { From = "/data/tv/", To = "/media/tv" },
            new() { From = "/data/anime", To = "D:\\Anime\\" },
            new() { From = "/data/tv/kids", To = "/kids" },
            new() { From = "", To = "/ignored" },
        ];
        Assert.Equal(expected, MediaServerPaths.Map(path, mappings));
    }

    [Fact]
    public void Picks_the_plex_section_containing_the_path()
    {
        var sections = PlexClient.Sections(System.Xml.Linq.XDocument.Parse("""
            <MediaContainer size="3">
              <Directory key="1" type="movie" title="Movies"><Location id="1" path="/media/movies" /></Directory>
              <Directory key="2" type="show" title="TV"><Location id="2" path="/media/tv" /><Location id="3" path="/media/tv2/" /></Directory>
              <Directory key="3" type="show" title="Kids"><Location id="4" path="/media/tv/kids" /></Directory>
            </MediaContainer>
            """));
        Assert.Equal("1", PlexClient.Match(sections, "/media/movies/Dune (2021)")?.Key);
        Assert.Equal("2", PlexClient.Match(sections, "/media/tv2/Show/Season 01")?.Key);
        Assert.Equal("3", PlexClient.Match(sections, "/media/tv/kids/Bluey")?.Key);
        Assert.Null(PlexClient.Match(sections, "/media/movies-4k/Dune"));
    }
}

public sealed class MediaServerRefreshQueueTests
{
    private readonly ManualTime _time = new(DateTimeOffset.Parse("2026-11-25T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Groups_changes_until_the_folder_is_quiet()
    {
        var queue = new MediaServerRefreshQueue(_time);
        // A season pack: ten episodes into the same season folder, a second apart.
        for (var i = 0; i < 10; i++)
        {
            queue.PathChanged("/tv/Show/Season 01/");
            _time.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(queue.TakeDue());
        }

        _time.Advance(MediaServerRefreshQueue.Quiet);
        var batch = queue.TakeDue();
        Assert.NotNull(batch);
        Assert.False(batch.Everything);
        Assert.Equal(["/tv/Show/Season 01"], batch.Folders);
        Assert.Null(queue.TakeDue());
    }

    [Fact]
    public void A_parent_folder_covers_its_subfolders()
    {
        var queue = new MediaServerRefreshQueue(_time);
        queue.PathChanged("/tv/Show/Season 01");
        queue.PathChanged("/tv/Show/Season 02");
        queue.PathChanged("/tv/Show");
        queue.PathChanged("/tv/Show/Season 03");
        queue.PathChanged("/tv/Show2");
        Assert.Equal(2, queue.PendingCount);

        _time.Advance(MediaServerRefreshQueue.Quiet);
        Assert.Equal(["/tv/Show", "/tv/Show2"], queue.TakeDue()!.Folders.Order());
    }

    [Fact]
    public void Steady_changes_still_flush_after_the_maximum_wait()
    {
        var queue = new MediaServerRefreshQueue(_time);
        var waited = TimeSpan.Zero;
        while (waited < MediaServerRefreshQueue.MaxWait)
        {
            queue.PathChanged("/movies/Busy");
            Assert.Null(queue.TakeDue());
            _time.Advance(TimeSpan.FromSeconds(5));
            waited += TimeSpan.FromSeconds(5);
        }

        Assert.Equal(["/movies/Busy"], queue.TakeDue()!.Folders);
    }

    [Fact]
    public void Too_many_folders_become_one_full_refresh()
    {
        var queue = new MediaServerRefreshQueue(_time);
        for (var i = 0; i <= MediaServerRefreshQueue.Capacity; i++) queue.PathChanged($"/movies/Movie {i}");
        Assert.Equal(0, queue.PendingCount);
        queue.PathChanged("/movies/One more");

        _time.Advance(MediaServerRefreshQueue.Quiet);
        var batch = queue.TakeDue();
        Assert.True(batch!.Everything);
        Assert.Empty(batch.Folders);
        Assert.Null(queue.TakeDue());
    }
}

public sealed class MediaServerClientTests
{
    private static readonly MediaServerRefreshBatch Folders = new(["/data/tv/Show/Season 01", "/data/movies/Dune (2021)"], false);

    [Fact]
    public async Task Jellyfin_gets_the_changed_folders_with_mapped_paths()
    {
        var server = new FakeMediaServer();
        var client = new MediaBrowserClient(new SingleHandlerFactory(server), NullLogger<MediaBrowserClient>.Instance);

        await client.RefreshAsync(MediaBrowserKind.Jellyfin, "http://jf:8096/base/", "secret", [new() { From = "/data", To = "/media" }], Folders, TestContext.Current.CancellationToken);

        var call = Assert.Single(server.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("/base/Library/Media/Updated", call.PathAndQuery);
        Assert.Equal("MediaBrowser Token=\"secret\"", call.Headers["Authorization"]);
        var updates = JsonDocument.Parse(call.Body!).RootElement.GetProperty("Updates").EnumerateArray()
            .Select(u => (u.GetProperty("Path").GetString(), u.GetProperty("UpdateType").GetString())).ToList();
        Assert.Equal([("/media/tv/Show/Season 01", "Modified"), ("/media/movies/Dune (2021)", "Modified")], updates);
    }

    [Fact]
    public async Task Emby_falls_back_to_a_library_refresh_when_folder_updates_fail()
    {
        var server = new FakeMediaServer { Respond = c => c.PathAndQuery == "/Library/Refresh" ? new HttpResponseMessage(HttpStatusCode.NoContent) : null };
        var client = new MediaBrowserClient(new SingleHandlerFactory(server), NullLogger<MediaBrowserClient>.Instance);

        await client.RefreshAsync(MediaBrowserKind.Emby, "http://emby:8096", "key", [], Folders, TestContext.Current.CancellationToken);

        Assert.Equal(["/Library/Media/Updated", "/Library/Refresh"], server.Calls.Select(c => c.PathAndQuery));
        Assert.All(server.Calls, c => Assert.Equal("key", c.Headers["X-Emby-Token"]));
        Assert.All(server.Calls, c => Assert.False(c.Headers.ContainsKey("Authorization")));
    }

    [Fact]
    public async Task A_full_batch_refreshes_the_whole_library()
    {
        var server = new FakeMediaServer();
        var client = new MediaBrowserClient(new SingleHandlerFactory(server), NullLogger<MediaBrowserClient>.Instance);
        await client.RefreshAsync(MediaBrowserKind.Jellyfin, "http://jf", "k", [], new([], true), TestContext.Current.CancellationToken);
        Assert.Equal("/Library/Refresh", Assert.Single(server.Calls).PathAndQuery);
    }

    private const string PlexSections = """
        <MediaContainer size="2">
          <Directory key="1" type="movie" title="Movies"><Location id="1" path="/media/movies" /></Directory>
          <Directory key="2" type="show" title="TV"><Location id="2" path="/media/tv" /></Directory>
        </MediaContainer>
        """;

    private static FakeMediaServer Plex() => new()
    {
        Respond = c => c.Headers.GetValueOrDefault("X-Plex-Token") != "token" ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : c.PathAndQuery == "/library/sections" ? FakeMediaServer.Xml(PlexSections)
            : c.PathAndQuery == "/" ? FakeMediaServer.Xml("""<MediaContainer friendlyName="Den" version="1.41.2" />""")
            : c.PathAndQuery.Contains("/refresh", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK)
            : null,
    };

    [Fact]
    public async Task Plex_refreshes_the_section_of_each_folder_by_path()
    {
        var server = Plex();
        var client = new PlexClient(new SingleHandlerFactory(server), NullLogger<PlexClient>.Instance);
        var settings = new PlexSettings { Url = "http://plex:32400", Token = "token", PathMappings = [new() { From = "/data", To = "/media" }] };

        await client.RefreshAsync(settings, new(["/data/tv/Show/Season 01", "/data/movies/Dune (2021)", "/elsewhere/Movie"], false), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["/library/sections", "/library/sections/2/refresh?path=%2Fmedia%2Ftv%2FShow%2FSeason%2001", "/library/sections/1/refresh?path=%2Fmedia%2Fmovies%2FDune%20%282021%29"],
            server.Calls.Select(c => c.PathAndQuery));
    }

    [Fact]
    public async Task Plex_refreshes_every_section_for_a_full_batch()
    {
        var server = Plex();
        var client = new PlexClient(new SingleHandlerFactory(server), NullLogger<PlexClient>.Instance);
        await client.RefreshAsync(new() { Url = "http://plex:32400", Token = "token" }, new([], true), TestContext.Current.CancellationToken);
        Assert.Equal(["/library/sections", "/library/sections/1/refresh", "/library/sections/2/refresh"], server.Calls.Select(c => c.PathAndQuery));
    }

    [Fact]
    public async Task Plex_test_reports_the_server_or_the_error()
    {
        var client = new PlexClient(new SingleHandlerFactory(Plex()), NullLogger<PlexClient>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var ok = await client.TestAsync(new() { Url = "http://plex:32400", Token = "token" }, ct);
        Assert.True(ok.Success);
        Assert.Equal(("Den", "1.41.2"), (ok.ServerName, ok.Version));

        var bad = await client.TestAsync(new() { Url = "http://plex:32400", Token = "wrong" }, ct);
        Assert.False(bad.Success);
        Assert.Contains("401", bad.Message, StringComparison.Ordinal);

        Assert.False((await client.TestAsync(new(), ct)).Success);
    }
}

public sealed class MediaServerFactory : KrakarrFactory
{
    public FakeMediaServer Jellyfin { get; } = new();
    public FakeMediaServer Emby { get; } = new();
    public FakeMediaServer Plex { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.AddHttpClient(JellyfinClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Jellyfin);
            s.AddHttpClient(MediaBrowserClient.EmbyHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Emby);
            s.AddHttpClient(PlexClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Plex);
        });
    }
}

public sealed class MediaServerEndpointTests(MediaServerFactory factory) : IClassFixture<MediaServerFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Saves_settings_tests_servers_and_refreshes_the_configured_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        factory.Emby.Respond = c => c.Headers.GetValueOrDefault("X-Emby-Token") != "emby-key" ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : c.PathAndQuery == "/System/Info" ? FakeMediaServer.Json("""{"ServerName":"Attic","Version":"4.8.10"}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent);
        factory.Plex.Respond = c => c.PathAndQuery == "/library/sections"
            ? FakeMediaServer.Xml("""<MediaContainer><Directory key="7" title="TV"><Location path="/tv" /></Directory></MediaContainer>""")
            : c.PathAndQuery == "/" ? FakeMediaServer.Xml("""<MediaContainer friendlyName="Den" version="1.41.2" />""")
            : new HttpResponseMessage(HttpStatusCode.OK);

        // Secrets are masked on the way out and kept when the mask comes back.
        var emby = await client.PutAsJsonAsync("/api/v1/settings/emby", new { url = "http://emby:8096", apiKey = "emby-key", pathMappings = new[] { new { from = "/data/tv", to = "/tv" } } }, ct);
        emby.EnsureSuccessStatusCode();
        Assert.Equal(EmbySettings.ApiKeyMask, (await emby.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("apiKey").GetString());
        var plex = await client.PutAsJsonAsync("/api/v1/settings/plex", new { url = "http://plex:32400", token = "plex-token", pathMappings = new[] { new { from = "/data/tv", to = "/tv" } } }, ct);
        plex.EnsureSuccessStatusCode();
        Assert.Equal(PlexSettings.TokenMask, (await plex.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString());
        (await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "http://jellyfin", apiKey = "jf", refreshLibrary = false }, ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/plex", new { url = "plex:32400" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/emby", new { url = "http://emby", pathMappings = new[] { new { from = "/data", to = "" } } }, ct)).StatusCode);

        var test = await (await client.PostAsJsonAsync("/api/v1/settings/emby/test", new { url = "http://emby:8096", apiKey = EmbySettings.ApiKeyMask }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(test.GetProperty("success").GetBoolean());
        Assert.Equal("Attic", test.GetProperty("serverName").GetString());
        var plexTest = await (await client.PostAsJsonAsync("/api/v1/settings/plex/test", new { url = "http://plex:32400", token = PlexSettings.TokenMask }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(plexTest.GetProperty("success").GetBoolean());
        Assert.Equal("plex-token", factory.Plex.Calls[^1].Headers["X-Plex-Token"]);

        // The host's notifier is the queue the refresher drains.
        Assert.IsType<MediaServerRefreshQueue>(factory.Services.GetRequiredService<IMediaServerNotifier>());
        factory.Emby.Calls.Clear();
        factory.Plex.Calls.Clear();
        await factory.Services.GetRequiredService<MediaServerRefresher>().RefreshAsync(new(["/data/tv/Show/Season 01"], false), ct);

        var update = Assert.Single(factory.Emby.Calls);
        Assert.Equal("/Library/Media/Updated", update.PathAndQuery);
        Assert.Contains("\"/tv/Show/Season 01\"", update.Body, StringComparison.Ordinal);
        Assert.Equal("/library/sections/7/refresh?path=%2Ftv%2FShow%2FSeason%2001", factory.Plex.Calls[^1].PathAndQuery);
        Assert.Empty(factory.Jellyfin.Calls); // refreshes turned off for Jellyfin
    }
}
