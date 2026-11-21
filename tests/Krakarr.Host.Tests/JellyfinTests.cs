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
            "/jf/System/Info" => """{"ServerName":"Living room","Version":"10.10.3"}""",
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
        int downloaded, missing, show, emptyShow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
            var m1 = new Movie { TmdbId = 7001, Title = "Jelly Movie", SortTitle = "jelly movie", Year = 2020, Path = "/movies/jelly", QualityProfileId = profile };
            var m2 = new Movie { TmdbId = 7002, Title = "Jelly Movie", SortTitle = "jelly movie", Year = 2020, Path = "/movies/jelly2", QualityProfileId = profile };
            var s1 = new Krakarr.Core.Domain.Series { TvdbId = 8001, Title = "Jelly Show", SortTitle = "jelly show", Year = 2019, Path = "/tv/jelly", QualityProfileId = profile };
            var s2 = new Krakarr.Core.Domain.Series { TvdbId = 8002, Title = "Empty Show", SortTitle = "empty show", Year = 2019, Path = "/tv/empty", QualityProfileId = profile };
            db.AddRange(m1, m2, s1, s2);
            await db.SaveChangesAsync(ct);
            m1.MovieFile = new MovieFile { MovieId = m1.Id, RelativePath = "jelly.mkv", QualityId = 17 };
            db.EpisodeFiles.Add(new EpisodeFile { SeriesId = s1.Id, SeasonNumber = 1, RelativePath = "s01e01.mkv", QualityId = 17 });
            await db.SaveChangesAsync(ct);
            (downloaded, missing, show, emptyShow) = (m1.Id, m2.Id, s1.Id, s2.Id);
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

        // Nothing downloaded: no link, without asking Jellyfin.
        var before = factory.Jellyfin.Requests.Count;
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/movies/{missing}/jellyfin", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/v1/series/{emptyShow}/jellyfin", ct)).StatusCode);
        Assert.Equal(before, factory.Jellyfin.Requests.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/movies/999999/jellyfin", ct)).StatusCode);
    }
}
