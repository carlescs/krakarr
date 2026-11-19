using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        var creds = new { username = "admin", password = "correct-horse" };
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
        var again = await NewClient().PostAsJsonAsync("/api/v1/auth/setup", new { username = "evil", password = "12345678" }, ct);
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
    public async Task Spa_fallback_does_not_swallow_unknown_api_routes()
    {
        var client = await LoggedInClientAsync();
        var res = await client.GetAsync("/api/v1/does-not-exist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
