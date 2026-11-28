using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class JellyfinMatchTests
{
    private static JellyfinItem Item(string id, string name, int? year, params (string Key, string Value)[] ids) =>
        new(id, name, "srv", year, ids.ToDictionary(i => i.Key, i => (string?)i.Value));

    [Fact]
    public void Prefers_a_provider_id_match_over_the_name()
    {
        var lookup = new JellyfinLookup(JellyfinItemKind.Movie, "Dune", null, 2021, 438631, null, "tt1160419");
        var items = new[] { Item("old", "Dune", 1984, ("Tmdb", "841")), Item("new", "Dune: Part One", 2021, ("Tmdb", "438631")) };
        Assert.Equal("new", JellyfinClient.Match(items, lookup)?.Id);
    }

    [Fact]
    public void Matches_ids_case_insensitively_and_by_imdb_or_tvdb()
    {
        var series = new JellyfinLookup(JellyfinItemKind.Series, "Breaking Bad", null, 2008, null, 81189, null);
        Assert.Equal("a", JellyfinClient.Match([Item("a", "Something else", 2008, ("tvdb", "81189"))], series)?.Id);
        var movie = new JellyfinLookup(JellyfinItemKind.Movie, "Alien", null, 1979, 348, null, "tt0078748");
        Assert.Equal("b", JellyfinClient.Match([Item("b", "Alien", 1979, ("IMDB", "TT0078748"))], movie)?.Id);
    }

    [Fact]
    public void Falls_back_to_name_and_year_but_not_onto_a_different_title()
    {
        var lookup = new JellyfinLookup(JellyfinItemKind.Movie, "Spider-Man: No Way Home", null, 2021, 634649, null, null);
        Assert.Equal("x", JellyfinClient.Match([Item("x", "Spider-Man No Way Home", 2022)], lookup)?.Id);
        Assert.Null(JellyfinClient.Match([Item("y", "Spider-Man: No Way Home", 2021, ("Tmdb", "1"))], lookup));
        Assert.Null(JellyfinClient.Match([Item("z", "Spider-Man: No Way Home", 2010)], lookup));
    }

    [Fact]
    public void Matches_episodes_by_id_then_by_number_including_multi_episode_files()
    {
        static JellyfinItem Ep(string id, int season, int number, int? end = null, params (string Key, string Value)[] ids) =>
            new(id, "Ep", "srv", null, ids.ToDictionary(i => i.Key, i => (string?)i.Value), season, number, end);
        var items = new[] { Ep("e1", 1, 1), Ep("e2", 1, 2, 3), Ep("e5", 1, 5, null, ("Tvdb", "555")) };

        Assert.Equal("e5", JellyfinClient.MatchEpisode(items, new(1, 4, null, 555))?.Id);
        Assert.Equal("e1", JellyfinClient.MatchEpisode(items, new(1, 1, null, 999))?.Id);
        Assert.Equal("e2", JellyfinClient.MatchEpisode(items, new(1, 3, null, null))?.Id);
        Assert.Null(JellyfinClient.MatchEpisode(items, new(1, 4, null, null)));
        Assert.Null(JellyfinClient.MatchEpisode(items, new(2, 1, null, null)));
    }

    [Fact]
    public void Matches_a_whole_series_of_episodes_at_once()
    {
        static JellyfinItem Ep(string id, int season, int number, int? end = null, params (string Key, string Value)[] ids) =>
            new(id, "Ep", "srv", null, ids.ToDictionary(i => i.Key, i => (string?)i.Value), season, number, end);
        var items = new[] { Ep("e1", 1, 1), Ep("e2", 1, 2, 3), Ep("e5", 1, 5, null, ("Tvdb", "555")), Ep("s2e1", 2, 1) };

        var matches = JellyfinClient.MatchEpisodes(items, [new(1, 4, null, 555), new(1, 1, null, 999), new(1, 3, null, null), new(1, 4, null, null), new(2, 1, null, null), new(3, 1, null, null)]);
        Assert.Equal(["e5", "e1", "e2", null, "s2e1", null], matches.Select(m => m?.Id));
    }

    [Fact]
    public void Reports_progress_only_while_part_way_watched()
    {
        static JellyfinItem Item(bool played, long position, long? runtime) =>
            new("x", "X", null, null, null, RunTimeTicks: runtime, UserData: new(played, played ? 1 : 0, position, null));
        Assert.Equal(0.25, PlayState.From("Jellyfin", Item(false, 250, 1000)).Progress);
        Assert.Null(PlayState.From("Jellyfin", Item(true, 250, 1000)).Progress);
        Assert.Null(PlayState.From("Jellyfin", Item(false, 250, null)).Progress);
        Assert.Null(PlayState.From("Jellyfin", Item(false, 0, 1000)).Progress);
        Assert.False(PlayState.From("Jellyfin", new JellyfinItem("x", "X", null, null, null)).Played);
    }

    [Fact]
    public async Task Reads_plex_items_with_guids_and_view_state()
    {
        var xml = """
            <MediaContainer size="2">
              <Video ratingKey="10" title="Pilot" parentIndex="1" index="1" duration="60000" viewCount="3" lastViewedAt="1700000000">
                <Media id="1"><Part id="1" file="/tv/a.mkv" /></Media>
                <Guid id="tvdb://555" /><Guid id="tmdb://66" />
              </Video>
              <Video ratingKey="11" title="Two" parentIndex="1" index="2" duration="60000" viewOffset="15000" />
              <Directory ratingKey="99" title="Not a video" />
            </MediaContainer>
            """;
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new() { Async = true });
        var items = await PlexClient.ReadItemsAsync(reader, "Video");

        Assert.Equal(2, items.Count);
        Assert.Equal("555", items[0].ProviderIds!["tvdb"]);
        Assert.Equal("66", items[0].ProviderIds!["Tmdb"]);
        var first = PlayState.From("Plex", items[0]);
        Assert.True(first.Played);
        Assert.Equal(3, first.PlayCount);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), first.LastPlayed);
        Assert.Equal(0.25, PlayState.From("Plex", items[1]).Progress);
        Assert.Equal((1, 2), (items[1].ParentIndexNumber, items[1].IndexNumber));
    }

    [Fact]
    public async Task Reads_movie_and_episode_plays_from_the_plex_history()
    {
        var xml = """
            <MediaContainer>
              <Video historyKey="/status/sessions/history/1" type="episode" ratingKey="73" grandparentKey="/library/metadata/7" parentIndex="2" index="4" viewedAt="1700000000" />
              <Video type="movie" ratingKey="5" viewedAt="1600000000" />
              <Video type="track" ratingKey="9" viewedAt="1600000000" />
              <Video type="movie" ratingKey="6" />
            </MediaContainer>
            """;
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), new() { Async = true });
        var plays = await PlexClient.ReadHistoryAsync(reader, 10);
        Assert.Equal([new PlexClient.PlexPlay(false, "7", 2, 4, DateTimeOffset.FromUnixTimeSeconds(1700000000)), new PlexClient.PlexPlay(true, "5", null, null, DateTimeOffset.FromUnixTimeSeconds(1600000000))], plays);
    }

    [Fact]
    public void Links_to_the_public_url_when_set()
    {
        var item = Item("abc", "Dune", 2021);
        Assert.Equal("http://jf:8096/web/#/details?id=abc&serverId=srv",
            JellyfinClient.ItemUrl(new() { Url = "http://jf:8096/", ApiKey = "k" }, item));
        Assert.Equal("https://tv.example.com/web/#/details?id=abc&serverId=srv",
            JellyfinClient.ItemUrl(new() { Url = "http://jf:8096", ApiKey = "k", PublicUrl = "https://tv.example.com/" }, item));
    }
}

/// <summary>Fake Jellyfin server: knows one movie and one series, and checks the API key.</summary>
public sealed class FakeJellyfin : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        lock (Requests) Requests.Add(path);
        if (request.Headers.Authorization?.Parameter != "Token=\"secret\"") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var body = path switch
        {
            var p when p.StartsWith("/jf/Shows/show-1/Episodes?Season=1&", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"ep-1","Name":"Pilot","ServerId":"srv","ParentIndexNumber":1,"IndexNumber":1},{"Id":"ep-2","Name":"Two","ServerId":"srv","ParentIndexNumber":1,"IndexNumber":2,"IndexNumberEnd":3}]}""",
            "/jf/System/Info" => """{"ServerName":"Living room","Version":"10.10.3"}""",
            "/jf/Users" => """[{"Name":"zoe","Id":"u2"},{"Name":"Anna","Id":"u1"}]""",
            var p when p.StartsWith("/jf/Items?UserId=u1&Recursive=true&IncludeItemTypes=Movie,Episode&SortBy=DatePlayed", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"ep-2","Type":"Episode","SeriesId":"show-1","ParentIndexNumber":1,"IndexNumber":2,"UserData":{"Played":false,"PlaybackPositionTicks":5,"LastPlayedDate":"2026-11-03T20:00:00Z"}},{"Id":"ep-1","Type":"Episode","SeriesId":"show-1","ParentIndexNumber":1,"IndexNumber":1,"UserData":{"Played":true,"LastPlayedDate":"2026-11-02T22:00:00Z"}},{"Id":"movie-1","Type":"Movie","ProviderIds":{"Tmdb":"7001"},"UserData":{"Played":true,"LastPlayedDate":"2026-11-02T20:00:00Z"}},{"Id":"other","Type":"Movie","ProviderIds":{"Tmdb":"1"},"UserData":{"Played":true,"LastPlayedDate":"2026-11-01T20:00:00Z"}},{"Id":"never","Type":"Movie","ProviderIds":{"Tmdb":"7001"},"UserData":{"Played":false}}]}""",
            var p when p.StartsWith("/jf/Items?Ids=show-1&", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"show-1","Type":"Series","Name":"Jelly Show","ProviderIds":{"Tvdb":"8001"}}]}""",
            var p when p.StartsWith("/jf/Shows/show-1/Episodes?UserId=u1&", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"ep-1","ParentIndexNumber":1,"IndexNumber":1,"UserData":{"Played":true,"PlayCount":2,"PlaybackPositionTicks":0,"LastPlayedDate":"2026-11-01T20:00:00Z"}},{"Id":"ep-2","ParentIndexNumber":1,"IndexNumber":2,"IndexNumberEnd":3,"RunTimeTicks":1000,"UserData":{"Played":false,"PlayCount":0,"PlaybackPositionTicks":250}}]}""",
            var p when p.StartsWith("/jf/Items?", StringComparison.Ordinal) && p.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal) && p.Contains("UserId=u1", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"movie-1","Name":"Jelly Movie","ServerId":"srv","ProductionYear":2020,"ProviderIds":{"Tmdb":"7001"},"RunTimeTicks":7200,"UserData":{"Played":false,"PlayCount":0,"PlaybackPositionTicks":1800}}]}""",
            var p when p.StartsWith("/jf/Items?", StringComparison.Ordinal) && p.Contains("IncludeItemTypes=Movie", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"movie-1","Name":"Jelly Movie","ServerId":"srv","ProductionYear":2020,"ProviderIds":{"Tmdb":"7001"}}]}""",
            var p when p.StartsWith("/jf/Items?", StringComparison.Ordinal) && p.Contains("IncludeItemTypes=Series", StringComparison.Ordinal) =>
                """{"Items":[{"Id":"show-1","Name":"Jelly Show","ServerId":"srv","ProductionYear":2019,"ProviderIds":{"Tvdb":"8001"}}]}""",
            _ => null,
        };
        return Task.FromResult(body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class JellyfinFactory : KrakarrFactory
{
    public FakeJellyfin Jellyfin { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddHttpClient(JellyfinClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Jellyfin));
    }
}

public sealed class JellyfinTests(JellyfinFactory factory) : IClassFixture<JellyfinFactory>
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
    public async Task Links_downloaded_titles_to_their_jellyfin_page()
    {
        var ct = TestContext.Current.CancellationToken;
        int downloaded, missing, show, emptyShow, episode, doubleEpisode, missingEpisode;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var m1 = new Movie { TmdbId = 7001, Title = "Jelly Movie", SortTitle = "jelly movie", Year = 2020, Path = "/movies/jelly", QualityProfileId = profile };
            var m2 = new Movie { TmdbId = 7002, Title = "Jelly Movie", SortTitle = "jelly movie", Year = 2020, Path = "/movies/jelly2", QualityProfileId = profile };
            var s1 = new Krakarr.Core.Domain.Series { TvdbId = 8001, Title = "Jelly Show", SortTitle = "jelly show", Year = 2019, Path = "/tv/jelly", QualityProfileId = profile };
            var s2 = new Krakarr.Core.Domain.Series { TvdbId = 8002, Title = "Empty Show", SortTitle = "empty show", Year = 2019, Path = "/tv/empty", QualityProfileId = profile };
            db.AddRange(m1, m2, s1, s2);
            await db.SaveChangesAsync(ct);
            m1.MovieFile = new MovieFile { MovieId = m1.Id, RelativePath = "jelly.mkv", QualityId = 17 };
            var f1 = new EpisodeFile { SeriesId = s1.Id, SeasonNumber = 1, RelativePath = "s01e01.mkv", QualityId = 17 };
            var f2 = new EpisodeFile { SeriesId = s1.Id, SeasonNumber = 1, RelativePath = "s01e02e03.mkv", QualityId = 17 };
            var e1 = new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 1, EpisodeFile = f1 };
            var e3 = new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 3, EpisodeFile = f2 };
            var e4 = new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 4 };
            db.AddRange(e1, e3, e4);
            await db.SaveChangesAsync(ct);
            (downloaded, missing, show, emptyShow) = (m1.Id, m2.Id, s1.Id, s2.Id);
            (episode, doubleEpisode, missingEpisode) = (e1.Id, e3.Id, e4.Id);
        }

        var client = await AdminAsync(ct);
        // Not configured yet: no link, and Jellyfin isn't contacted.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/movies/{downloaded}/jellyfin", ct)).StatusCode);
        Assert.Empty(factory.Jellyfin.Requests);

        var saved = await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "http://jellyfin/jf", apiKey = "secret", publicUrl = "https://tv.example.com" }, ct);
        saved.EnsureSuccessStatusCode();
        Assert.Equal(JellyfinSettings.ApiKeyMask, (await saved.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("apiKey").GetString());
        // Sending the mask back keeps the stored key.
        (await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "http://jellyfin/jf", apiKey = JellyfinSettings.ApiKeyMask, publicUrl = "https://tv.example.com" }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "jellyfin:8096" }, ct)).StatusCode);

        var test = await (await client.PostAsJsonAsync("/api/v1/settings/jellyfin/test", new { url = "http://jellyfin/jf", apiKey = JellyfinSettings.ApiKeyMask }, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(test.GetProperty("success").GetBoolean());
        Assert.Equal("10.10.3", test.GetProperty("version").GetString());

        var movieLink = await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies/{downloaded}/jellyfin", ct);
        Assert.Equal("https://tv.example.com/web/#/details?id=movie-1&serverId=srv", movieLink.GetProperty("url").GetString());
        var showLink = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{show}/jellyfin", ct);
        Assert.Equal("https://tv.example.com/web/#/details?id=show-1&serverId=srv", showLink.GetProperty("url").GetString());

        var episodeLink = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{show}/episodes/{episode}/jellyfin", ct);
        Assert.Equal("https://tv.example.com/web/#/details?id=ep-1&serverId=srv", episodeLink.GetProperty("url").GetString());
        var doubleLink = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{show}/episodes/{doubleEpisode}/jellyfin", ct);
        Assert.Equal("https://tv.example.com/web/#/details?id=ep-2&serverId=srv", doubleLink.GetProperty("url").GetString());

        // Nothing downloaded: no link, without asking Jellyfin.
        var before = factory.Jellyfin.Requests.Count;
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/series/{show}/episodes/{missingEpisode}/jellyfin", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/series/{emptyShow}/episodes/{episode}/jellyfin", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/movies/{missing}/jellyfin", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/series/{emptyShow}/jellyfin", ct)).StatusCode);
        Assert.Equal(before, factory.Jellyfin.Requests.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/movies/999999/jellyfin", ct)).StatusCode);
    }
}

public sealed class PlayStatusFactory : KrakarrFactory
{
    public FakeJellyfin Jellyfin { get; } = new();

    public FakeMediaServer Plex { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s =>
        {
            s.AddHttpClient(JellyfinClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Jellyfin);
            s.AddHttpClient(PlexClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Plex);
        });
    }
}

public sealed class PlayStatusTests(PlayStatusFactory factory) : IClassFixture<PlayStatusFactory>
{
    [Fact]
    public async Task Reports_watched_state_from_jellyfin_and_plex()
    {
        var ct = TestContext.Current.CancellationToken;
        factory.Plex.Respond = call => call.PathAndQuery switch
        {
            "/library/sections" => FakeMediaServer.Xml("""
                <MediaContainer><Directory key="1" type="movie" title="Movies"><Location path="/m" /></Directory><Directory key="2" type="show" title="TV"><Location path="/t" /></Directory></MediaContainer>
                """),
            var p when p.StartsWith("/library/sections/1/all?type=1&includeGuids=1&title=Jelly%20Movie", StringComparison.Ordinal) => FakeMediaServer.Xml("""
                <MediaContainer><Video ratingKey="5" title="Jelly Movie" year="2020" viewCount="1" duration="1000"><Guid id="tmdb://7001" /></Video></MediaContainer>
                """),
            var p when p.StartsWith("/library/sections/2/all?type=2&includeGuids=1&title=Jelly%20Show", StringComparison.Ordinal) => FakeMediaServer.Xml("""
                <MediaContainer><Directory ratingKey="7" title="Jelly Show" year="2019"><Guid id="tvdb://8001" /></Directory></MediaContainer>
                """),
            var p when p.StartsWith("/status/sessions/history/all?sort=viewedAt:desc&accountID=1&", StringComparison.Ordinal) => FakeMediaServer.Xml("""
                <MediaContainer><Video type="episode" grandparentKey="/library/metadata/7" parentIndex="1" index="3" viewedAt="1700000000" /><Video type="movie" ratingKey="5" viewedAt="1600000000" /><Video type="track" ratingKey="9" viewedAt="1800000000" /></MediaContainer>
                """),
            "/library/metadata/7,5?includeGuids=1" => FakeMediaServer.Xml("""
                <MediaContainer><Directory ratingKey="7" title="Jelly Show"><Guid id="tvdb://8001" /></Directory><Video ratingKey="5" title="Jelly Movie"><Guid id="tmdb://7001" /></Video></MediaContainer>
                """),
            "/library/metadata/7/allLeaves?includeGuids=1" => FakeMediaServer.Xml("""
                <MediaContainer><Video ratingKey="71" parentIndex="1" index="1" duration="1000" /><Video ratingKey="73" parentIndex="1" index="3" viewCount="1" duration="1000" /></MediaContainer>
                """),
            _ => null,
        };

        int movie, show, e1, e3;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var m1 = new Movie { TmdbId = 7001, Title = "Jelly Movie", SortTitle = "jelly movie", Year = 2020, Path = "/movies/jelly", QualityProfileId = profile };
            var s1 = new Krakarr.Core.Domain.Series { TvdbId = 8001, Title = "Jelly Show", SortTitle = "jelly show", Year = 2019, Path = "/tv/jelly", QualityProfileId = profile };
            db.AddRange(m1, s1);
            await db.SaveChangesAsync(ct);
            m1.MovieFile = new MovieFile { MovieId = m1.Id, RelativePath = "jelly.mkv", QualityId = 17 };
            var f1 = new EpisodeFile { SeriesId = s1.Id, SeasonNumber = 1, RelativePath = "s01e01.mkv", QualityId = 17 };
            var f2 = new EpisodeFile { SeriesId = s1.Id, SeasonNumber = 1, RelativePath = "s01e02e03.mkv", QualityId = 17 };
            var ep1 = new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 1, EpisodeFile = f1 };
            var ep3 = new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 3, EpisodeFile = f2 };
            db.AddRange(ep1, ep3, new Episode { SeriesId = s1.Id, SeasonNumber = 1, EpisodeNumber = 4 });
            await db.SaveChangesAsync(ct);
            (movie, show, e1, e3) = (m1.Id, s1.Id, ep1.Id, ep3.Id);
        }

        var client = factory.CreateClient(new() { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        // Nothing set up to report play status yet.
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/movies/{movie}/playstatus", ct) ?? []);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard/recently-played", ct)).GetProperty("enabled").GetBoolean());

        (await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "http://jellyfin/jf", apiKey = "secret" }, ct)).EnsureSuccessStatusCode();
        var users = await (await client.PostAsJsonAsync("/api/v1/settings/jellyfin/users", new { url = "http://jellyfin/jf", apiKey = JellyfinSettings.ApiKeyMask }, ct)).Content.ReadFromJsonAsync<JsonElement[]>(ct);
        Assert.Equal(["Anna", "zoe"], users!.Select(u => u.GetProperty("name").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/settings/jellyfin/users", new { url = "" }, ct)).StatusCode);

        (await client.PutAsJsonAsync("/api/v1/settings/jellyfin", new { url = "http://jellyfin/jf", apiKey = JellyfinSettings.ApiKeyMask, playStatusUserId = "u1" }, ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync("/api/v1/settings/plex", new { url = "http://plex:32400", token = "tok", playStatus = true }, ct)).EnsureSuccessStatusCode();

        var movieStates = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/movies/{movie}/playstatus", ct);
        var jf = movieStates!.Single(s => s.GetProperty("server").GetString() == "Jellyfin");
        Assert.False(jf.GetProperty("played").GetBoolean());
        Assert.Equal(0.25, jf.GetProperty("progress").GetDouble());
        Assert.True(movieStates!.Single(s => s.GetProperty("server").GetString() == "Plex").GetProperty("played").GetBoolean());

        var seriesStates = await client.GetFromJsonAsync<JsonElement[]>($"/api/v1/series/{show}/playstatus", ct);
        var jfShow = seriesStates!.Single(s => s.GetProperty("server").GetString() == "Jellyfin");
        Assert.Equal((1, 2), (jfShow.GetProperty("watched").GetInt32(), jfShow.GetProperty("total").GetInt32()));
        var jfEpisodes = jfShow.GetProperty("episodes").EnumerateArray().ToDictionary(e => e.GetProperty("episodeId").GetInt32());
        Assert.True(jfEpisodes[e1].GetProperty("played").GetBoolean());
        Assert.Equal(0.25, jfEpisodes[e3].GetProperty("progress").GetDouble()); // the multi-episode file covers E03
        var plexShow = seriesStates!.Single(s => s.GetProperty("server").GetString() == "Plex");
        Assert.Equal(e3, plexShow.GetProperty("episodes").EnumerateArray().Single().GetProperty("episodeId").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/movies/999999/playstatus", ct)).StatusCode);

        // Recently played: newest first, one entry per library title, titles not in the library left out.
        var recent = await client.GetFromJsonAsync<JsonElement>("/api/v1/dashboard/recently-played", ct);
        Assert.True(recent.GetProperty("enabled").GetBoolean());
        var items = recent.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([("Series", show, "Jellyfin"), ("Movie", movie, "Jellyfin")],
            items.Select(i => (i.GetProperty("mediaType").GetString(), i.GetProperty("id").GetInt32(), i.GetProperty("server").GetString())));
        Assert.Equal((1, 2), (items[0].GetProperty("seasonNumber").GetInt32(), items[0].GetProperty("episodeNumber").GetInt32()));
        Assert.False(items[0].GetProperty("finished").GetBoolean());
    }
}
