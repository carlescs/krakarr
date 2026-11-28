using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class OmdbRefreshFactory : KrakarrFactory
{
    public FakeOmdb Omdb { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(s => s.AddHttpClient(OmdbClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Omdb));
    }
}

public sealed class OmdbRefreshTests(OmdbRefreshFactory factory) : IClassFixture<OmdbRefreshFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task RunTaskAsync(CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OmdbRefreshTask>().ExecuteAsync(ct);
    }

    [Fact]
    public async Task Stores_scores_and_facts_that_the_library_sorts_and_filters_by()
    {
        var ct = TestContext.Current.CancellationToken;
        int matrixId, unknownId, officeId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var matrixRow = new Movie { TmdbId = 603, ImdbId = "tt0133093", Title = "The Matrix", Year = 1999, Path = "/movies/matrix", QualityProfileId = profile };
            var unknownRow = new Movie { TmdbId = 90_001, ImdbId = "tt9999999", Title = "Aaa Unknown", Year = 2020, Overview = "From TMDB", Path = "/movies/unknown", QualityProfileId = profile };
            var noImdb = new Movie { TmdbId = 90_002, Title = "No IMDb", Year = 2021, Path = "/movies/noimdb", QualityProfileId = profile };
            var officeRow = new Core.Domain.Series { TmdbId = 2316, ImdbId = "tt0386676", Title = "The Office", MetadataTitle = "The Office", Path = "/tv/office", QualityProfileId = profile };
            db.Movies.AddRange(matrixRow, unknownRow, noImdb);
            db.Series.Add(officeRow);
            await db.SaveChangesAsync(ct);
            (matrixId, unknownId, officeId) = (matrixRow.Id, unknownRow.Id, officeRow.Id);
        }

        var client = await AdminAsync(ct);

        // No key: the task does nothing.
        await RunTaskAsync(ct);
        Assert.Empty(factory.Omdb.Requests);

        var meta = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/metadata", ct);
        Assert.Equal(500, meta.GetProperty("omdbDailyLimit").GetInt32());
        var updated = JsonSerializer.Deserialize<Dictionary<string, object?>>(meta.GetRawText())!;
        updated["omdbApiKey"] = "omdb-key";
        (await client.PutAsJsonAsync("/api/v1/settings/metadata", updated, ct)).EnsureSuccessStatusCode();

        await RunTaskAsync(ct);
        // Three titles have IMDb ids; the one without is never asked about.
        Assert.Equal(3, factory.Omdb.Requests.Count);

        var matrix = await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies/{matrixId}", ct);
        var omdb = matrix.GetProperty("omdb");
        Assert.Equal(8.7, omdb.GetProperty("imdb").GetDouble());
        Assert.Equal(83, omdb.GetProperty("rottenTomatoes").GetInt32());
        Assert.Equal("R", omdb.GetProperty("rated").GetString());
        Assert.Equal("$172,076,928", omdb.GetProperty("boxOffice").GetString());
        // TMDB gave no overview: OMDb's plot fills in.
        Assert.Equal("A computer hacker learns about the true nature of reality.", matrix.GetProperty("overview").GetString());

        // OMDb doesn't know it: stored as looked up, with no scores, and the TMDB overview stays.
        var unknown = await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies/{unknownId}", ct);
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("omdb").GetProperty("imdb").ValueKind);
        Assert.Equal("From TMDB", unknown.GetProperty("overview").GetString());

        var office = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{officeId}", ct);
        Assert.Equal("TV-14", office.GetProperty("omdb").GetProperty("rated").GetString());
        Assert.Equal(JsonValueKind.Null, office.GetProperty("omdb").GetProperty("director").ValueKind); // "N/A"

        // Sorted by IMDb rating, unrated movies last whatever the direction.
        var byRating = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?sort=imdb&desc=true", ct);
        var titles = byRating.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("title").GetString()).ToList();
        Assert.Equal("The Matrix", titles[0]);
        Assert.Equal(8.7, byRating.GetProperty("items")[0].GetProperty("imdbRating").GetDouble());
        var ascending = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?sort=imdb", ct);
        Assert.Equal("The Matrix", ascending.GetProperty("items")[0].GetProperty("title").GetString());

        var good = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?minRating=8", ct);
        Assert.Equal(["The Matrix"], good.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("title").GetString()));
        Assert.Equal([matrixId], (await client.GetFromJsonAsync<int[]>("/api/v1/movies/ids?minRating=8", ct))!);
        var goodShows = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?minRating=8.5&sort=imdb&desc=true", ct);
        Assert.Equal(9.0, goodShows.GetProperty("items").EnumerateArray().Single().GetProperty("imdbRating").GetDouble());

        // Everything is fresh now: the next run asks OMDb nothing.
        await RunTaskAsync(ct);
        Assert.Equal(3, factory.Omdb.Requests.Count);
    }

    [Fact]
    public void Budget_spreads_the_daily_limit_over_hourly_runs()
    {
        Assert.Equal(21, OmdbRefreshTask.Budget(500));
        Assert.Equal(1, OmdbRefreshTask.Budget(5));
        Assert.Equal(0, OmdbRefreshTask.Budget(0));
    }
}
