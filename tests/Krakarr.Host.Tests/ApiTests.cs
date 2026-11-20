using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class ApiTests : IClassFixture<KrakarrFactory>
{
    private readonly KrakarrFactory _factory;

    public ApiTests(KrakarrFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new() { HandleCookies = true });

    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = NewClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", TestContext.Current.CancellationToken);
        var creds = new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken };
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        var res = await client.PostAsJsonAsync(url, creds, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var res = await NewClient().GetAsync("/api/v1/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Api_requires_authentication()
    {
        var res = await NewClient().GetAsync("/api/v1/movies", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Setup_then_login_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/movies", ct)).StatusCode);

        // Setup can only run once.
        var again = await NewClient().PostAsJsonAsync("/api/v1/auth/setup", new { username = "evil", password = "12345678", setupToken = KrakarrFactory.TestSetupToken }, ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var wrong = await NewClient().PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = "nope-nope" }, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Api_key_authenticates()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var general = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/general", ct);
        var key = general.GetProperty("apiKey").GetString();
        Assert.False(string.IsNullOrEmpty(key));

        var apiClient = NewClient();
        apiClient.DefaultRequestHeaders.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/v1/qualityprofiles", ct)).StatusCode);

        var bad = NewClient();
        bad.DefaultRequestHeaders.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bad.GetAsync("/api/v1/qualityprofiles", ct)).StatusCode);
    }

    [Fact]
    public async Task Seeds_quality_profiles_and_validates_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var profiles = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/qualityprofiles", ct);
        Assert.Equal(4, profiles!.Length);

        var badIndexer = await client.PostAsJsonAsync("/api/v1/indexers", new { name = "", baseUrl = "not a url" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badIndexer.StatusCode);

        var badProfile = await client.PostAsJsonAsync("/api/v1/qualityprofiles", new { name = "x", allowedQualityIds = new[] { 16 }, cutoffQualityId = 17 }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badProfile.StatusCode);
    }

    [Fact]
    public async Task Settings_round_trip_and_api_key_is_read_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var original = (await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/general", ct)).GetProperty("apiKey").GetString();

        var put = await client.PutAsJsonAsync("/api/v1/settings/general", new { apiKey = "hijack", instanceName = "Test" }, ct);
        put.EnsureSuccessStatusCode();
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/general", ct);
        Assert.Equal(original, after.GetProperty("apiKey").GetString());
        Assert.Equal("Test", after.GetProperty("instanceName").GetString());

        var media = await client.PutAsJsonAsync("/api/v1/settings/media", new { renameFiles = false, movieFolderFormat = "{Movie Title}", movieFileFormat = "{Movie Title}", useHardlinks = true, minimumVideoFileSizeMb = 10 }, ct);
        media.EnsureSuccessStatusCode();
        var mediaAfter = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/media", ct);
        Assert.False(mediaAfter.GetProperty("renameFiles").GetBoolean());
    }

    [Fact]
    public async Task Wanted_languages_are_normalized_and_published_with_the_catalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var put = await client.PutAsJsonAsync("/api/v1/settings/languages", new { preferred = new[] { "ES", "klingon", "en", "es" } }, ct);
        put.EnsureSuccessStatusCode();

        var languages = await client.GetFromJsonAsync<JsonElement>("/api/v1/languages", ct);
        Assert.Equal(["es", "en"], languages.GetProperty("preferred").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(languages.GetProperty("all").EnumerateArray(), l => l.GetProperty("code").GetString() == "fr");

        (await client.PutAsJsonAsync("/api/v1/settings/languages", new { preferred = Array.Empty<string>() }, ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Spa_fallback_does_not_swallow_unknown_api_routes()
    {
        var client = await LoggedInClientAsync();
        var res = await client.GetAsync("/api/v1/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task QBittorrent_password_is_masked_and_preserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var settings = new
        {
            client = "QBittorrent",
            qBittorrent = new { url = "http://qbt:8080", username = "admin", password = "hunter22", tag = "krakarr" },
        };
        (await client.PutAsJsonAsync("/api/v1/settings/downloads", settings, ct)).EnsureSuccessStatusCode();

        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/downloads", ct);
        Assert.Equal("QBittorrent", read.GetProperty("client").GetString());
        Assert.Equal("********", read.GetProperty("qBittorrent").GetProperty("password").GetString());

        // Saving the masked value back keeps the real password (verified through the stored settings).
        var masked = new { client = "QBittorrent", qBittorrent = new { url = "http://qbt:8080", username = "admin", password = "********", tag = "krakarr" } };
        (await client.PutAsJsonAsync("/api/v1/settings/downloads", masked, ct)).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<Krakarr.Data.ISettingsService>()
            .GetAsync<Krakarr.Core.Configuration.DownloadClientSettings>(ct);
        Assert.Equal("hunter22", stored.QBittorrent.Password);

        var noTag = new { client = "QBittorrent", qBittorrent = new { url = "http://qbt:8080", tag = "" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/settings/downloads", noTag, ct)).StatusCode);

        // Reset so other tests keep using the built-in client.
        (await client.PutAsJsonAsync("/api/v1/settings/downloads", new { client = "Embedded" }, ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Repeated_failed_logins_lock_the_username()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = NewClient();
        for (var i = 0; i < 5; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "ghost", password = "wrong-password" }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "Ghost ", password = "wrong-password" }, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task Deleting_files_outside_root_folders_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var outside = Directory.CreateTempSubdirectory("krakarr-outside");
        File.WriteAllText(Path.Combine(outside.FullName, "keep.txt"), "x");
        try
        {
            int id;
            using (var scope = _factory.Services.CreateScope())
            {
                // Simulates a movie stored with an arbitrary path (e.g. added before path validation existed).
                var db = scope.ServiceProvider.GetRequiredService<Krakarr.Data.KrakarrDbContext>();
                var movie = new Krakarr.Core.Domain.Movie { TmdbId = 987654, Title = "Evil", Path = outside.FullName, QualityProfileId = 1 };
                db.Movies.Add(movie);
                await db.SaveChangesAsync(ct);
                id = movie.Id;
            }

            var res = await client.DeleteAsync($"/api/v1/movies/{id}?deleteFiles=true", ct);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.True(File.Exists(Path.Combine(outside.FullName, "keep.txt")));
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/movies/{id}", ct)).StatusCode);
        }
        finally
        {
            outside.Delete(true);
        }
    }

    [Fact]
    public async Task Manual_download_link_must_point_to_a_library_item()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Some.Movie.2020.1080p";

        var unknown = await client.PostAsJsonAsync("/api/v1/downloads/magnet", new { magnet, movieId = 424242 }, ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var both = await client.PostAsJsonAsync("/api/v1/downloads/magnet", new { magnet, movieId = 1, seriesId = 1 }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }
}
