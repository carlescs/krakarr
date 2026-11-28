using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Krakarr.Host.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Fake OMDb: knows The Matrix, says "not found" for everything else, and checks the API key.</summary>
public sealed class FakeOmdb : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = request.RequestUri!.Query;
        lock (Requests) Requests.Add(query);
        string body;
        var status = HttpStatusCode.OK;
        if (!query.Contains("apikey=omdb-key", StringComparison.Ordinal))
        {
            (status, body) = (HttpStatusCode.Unauthorized, """{"Response":"False","Error":"Invalid API key!"}""");
        }
        else if (query.Contains("i=tt0133093", StringComparison.Ordinal))
        {
            body = """
                {"Title":"The Matrix","Rated":"R","Director":"Lana Wachowski, Lilly Wachowski","Writer":"Lilly Wachowski, Lana Wachowski",
                 "Plot":"A computer hacker learns about the true nature of reality.","Awards":"Won 4 Oscars. 42 wins & 51 nominations total",
                 "Ratings":[{"Source":"Internet Movie Database","Value":"8.7/10"},{"Source":"Rotten Tomatoes","Value":"83%"},{"Source":"Metacritic","Value":"73/100"}],
                 "Metascore":"73","imdbRating":"8.7","imdbVotes":"2,123,456","imdbID":"tt0133093","BoxOffice":"$172,076,928",
                 "tomatoURL":"https://www.rottentomatoes.com/m/matrix/","Response":"True"}
                """;
        }
        else if (query.Contains("i=tt0386676", StringComparison.Ordinal))
        {
            body = """
                {"Title":"The Office","Rated":"TV-14","Director":"N/A","Writer":"Greg Daniels","Plot":"N/A","Awards":"N/A","BoxOffice":"N/A",
                 "Ratings":[{"Source":"Internet Movie Database","Value":"9.0/10"}],"Metascore":"N/A","imdbRating":"9.0","imdbVotes":"750,000",
                 "imdbID":"tt0386676","Type":"series","Response":"True"}
                """;
        }
        else
        {
            body = """{"Response":"False","Error":"Incorrect IMDb ID."}""";
        }

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class RatingsFactory : KrakarrFactory
{
    public FakeOmdb Omdb { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddHttpClient(OmdbClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Omdb));
    }
}

public sealed class RatingsTests(RatingsFactory factory) : IClassFixture<RatingsFactory>
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
    public async Task Returns_imdb_rotten_tomatoes_and_metacritic_scores_and_caches_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        // No key yet: nothing to show, and OMDb isn't contacted.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/ratings/tt0133093", ct)).StatusCode);
        Assert.Empty(factory.Omdb.Requests);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/ratings/not-an-id", ct)).StatusCode);

        var meta = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/metadata", ct);
        var updated = JsonSerializer.Deserialize<Dictionary<string, object?>>(meta.GetRawText())!;
        updated["omdbApiKey"] = "omdb-key";
        (await client.PutAsJsonAsync("/api/v1/settings/metadata", updated, ct)).EnsureSuccessStatusCode();

        var ratings = await client.GetFromJsonAsync<JsonElement>("/api/v1/ratings/tt0133093", ct);
        Assert.Equal(8.7, ratings.GetProperty("imdb").GetDouble());
        Assert.Equal(2123456, ratings.GetProperty("imdbVotes").GetInt64());
        Assert.Equal(83, ratings.GetProperty("rottenTomatoes").GetInt32());
        Assert.Equal(73, ratings.GetProperty("metacritic").GetInt32());
        Assert.Equal("https://www.rottentomatoes.com/m/matrix/", ratings.GetProperty("rottenTomatoesUrl").GetString());
        Assert.Equal("R", ratings.GetProperty("rated").GetString());
        Assert.Equal("Won 4 Oscars. 42 wins & 51 nominations total", ratings.GetProperty("awards").GetString());
        Assert.Equal("$172,076,928", ratings.GetProperty("boxOffice").GetString());
        Assert.Equal("Lana Wachowski, Lilly Wachowski", ratings.GetProperty("director").GetString());

        // Served from the cache the second time.
        var before = factory.Omdb.Requests.Count;
        (await client.GetAsync("/api/v1/ratings/tt0133093", ct)).EnsureSuccessStatusCode();
        Assert.Equal(before, factory.Omdb.Requests.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/ratings/tt9999999", ct)).StatusCode);
    }

    [Fact]
    public void Plain_users_may_read_ratings()
    {
        Assert.True(Krakarr.Host.Auth.RoleAccess.IsAllowed(Krakarr.Core.Domain.UserRole.User, "GET", "/api/v1/ratings/tt0133093"));
    }
}
