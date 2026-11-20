using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Movies;
using Krakarr.Series;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Own host with fake TMDB clients, so requests can be created without network access.</summary>
public sealed class RequestsFactory : KrakarrFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ITmdbClient, FakeTmdb>();
            services.AddSingleton<ISeriesDiscovery, FakeSeriesDiscovery>();
        });
    }

    private sealed class FakeTmdb : ITmdbClient
    {
        public Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MovieLookup?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default) => tmdbId == 404
            ? throw new MetadataException("TMDB has no such movie")
            : Task.FromResult(new MovieLookup { TmdbId = tmdbId, Title = $"Movie {tmdbId}", Year = 2020, PosterUrl = "https://image.tmdb.org/p.jpg" });
    }

    private sealed class FakeSeriesDiscovery : ISeriesDiscovery
    {
        public Task<SeriesDetails> DetailsAsync(int tmdbId, CancellationToken ct = default) =>
            Task.FromResult(new SeriesDetails { Show = new SeriesLookup { TmdbId = tmdbId, TvdbId = tmdbId + 100_000, Title = $"Show {tmdbId}", Year = 2019 } });

        public Task<DiscoverPage<SeriesLookup>> ListAsync(SeriesList list, int page, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SeriesLookup>> RecommendationsAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SeriesLookup>> SimilarAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IReadOnlyList<SeriesLookup>>> RecommendationsAsync(IReadOnlyList<SeriesIds> shows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Credits> CreditsAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

public sealed class RequestTests(RequestsFactory factory) : IClassFixture<RequestsFactory>
{
    private const string AdminPassword = "correct-horse";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient NewClient() => factory.CreateClient(new() { HandleCookies = true });

    private async Task<HttpClient> AdminAsync()
    {
        var client = NewClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        var creds = new { username = "admin", password = AdminPassword, setupToken = KrakarrFactory.TestSetupToken };
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(url, creds, Ct)).StatusCode);
        return client;
    }

    private async Task<HttpClient> UserAsync(string username, string role = "User")
    {
        var admin = await AdminAsync();
        var res = await admin.PostAsJsonAsync("/api/v1/users", new { username, password = "password-123", role }, Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var client = NewClient();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = "password-123" }, Ct)).StatusCode);
        return client;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string mediaType, int tmdbId)
    {
        var res = await client.PostAsJsonAsync("/api/v1/requests", new { mediaType, tmdbId }, Ct);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<int[]> ListIdsAsync(HttpClient client, string? status = null)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/requests" + (status is null ? "" : $"?status={status}"), Ct);
        return page.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToArray();
    }

    [Fact]
    public async Task Users_request_titles_and_staff_decline_them()
    {
        var alice = await UserAsync("alice");
        var bob = await UserAsync("bob");
        var manager = await UserAsync("mona", "Manager");

        var request = await CreateAsync(alice, "Movie", 1001);
        var id = request.GetProperty("id").GetInt32();
        Assert.Equal("Movie 1001", request.GetProperty("title").GetString());
        Assert.Equal("Pending", request.GetProperty("status").GetString());
        Assert.Equal("alice", request.GetProperty("requestedBy").GetString());

        var show = await CreateAsync(bob, "Series", 77);
        Assert.Equal(100_077, show.GetProperty("tvdbId").GetInt32());

        // One open request per title, whoever asks.
        Assert.Equal(HttpStatusCode.Conflict, (await bob.PostAsJsonAsync("/api/v1/requests", new { mediaType = "Movie", tmdbId = 1001 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.PostAsJsonAsync("/api/v1/requests", new { mediaType = "Movie", tmdbId = 404 }, Ct)).StatusCode);

        // Users see their own requests; staff see everyone's.
        Assert.Contains(id, await ListIdsAsync(alice));
        Assert.DoesNotContain(id, await ListIdsAsync(bob));
        Assert.Contains(id, await ListIdsAsync(manager, "pending"));
        Assert.Equal(1, (await alice.GetFromJsonAsync<JsonElement>("/api/v1/requests/count", Ct)).GetProperty("pending").GetInt32());

        // Anyone can see that a title has been requested.
        var open = await bob.GetFromJsonAsync<JsonElement>("/api/v1/requests/tmdb/Movie/1001", Ct);
        Assert.Equal(id, open.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await bob.GetAsync("/api/v1/requests/tmdb/Movie/999999", Ct)).StatusCode);

        // Plain users can't approve, decline or remove someone else's request.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync($"/api/v1/requests/{id}/approve", new { qualityProfileId = 1, rootFolderPath = "/x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync($"/api/v1/requests/{id}/decline", new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/v1/requests/{id}", Ct)).StatusCode);

        var declined = await manager.PostAsJsonAsync($"/api/v1/requests/{id}/decline", new { reason = "Not on any indexer" }, Ct);
        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        var mine = await alice.GetFromJsonAsync<JsonElement>("/api/v1/requests?status=declined", Ct);
        var row = mine.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == id);
        Assert.Equal("Not on any indexer", row.GetProperty("declineReason").GetString());
        Assert.Equal("mona", row.GetProperty("processedBy").GetString());

        // Declined requests can't be withdrawn by the user, but the title can be requested again.
        Assert.Equal(HttpStatusCode.Conflict, (await alice.DeleteAsync($"/api/v1/requests/{id}", Ct)).StatusCode);
        var again = await CreateAsync(alice, "Movie", 1001);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/requests/{again.GetProperty("id").GetInt32()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Approving_adds_the_title_and_direct_adds_fulfil_requests()
    {
        var carol = await UserAsync("carol");
        var admin = await AdminAsync();
        var root = Path.Combine(factory.ConfigDirectory, "movies");
        Directory.CreateDirectory(root);
        int profile;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            db.RootFolders.Add(new RootFolder { Path = root, MediaType = MediaType.Movie });
            await db.SaveChangesAsync(Ct);
            profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(Ct);
        }

        var id = (await CreateAsync(carol, "Movie", 2001)).GetProperty("id").GetInt32();
        var approved = await admin.PostAsJsonAsync($"/api/v1/requests/{id}/approve",
            new { qualityProfileId = profile, rootFolderPath = root, searchNow = false }, Ct);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var dto = await approved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Approved", dto.GetProperty("status").GetString());
        Assert.Equal("admin", dto.GetProperty("processedBy").GetString());
        Assert.Equal("Missing", dto.GetProperty("availability").GetString());
        var movieId = dto.GetProperty("libraryId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await carol.GetAsync($"/api/v1/movies/{movieId}", Ct)).StatusCode);

        // In the library now: no new requests, and approving twice is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await carol.PostAsJsonAsync("/api/v1/requests", new { mediaType = "Movie", tmdbId = 2001 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/requests/{id}/approve",
            new { qualityProfileId = profile, rootFolderPath = root }, Ct)).StatusCode);

        // Adding a requested title directly approves its pending request.
        var other = (await CreateAsync(carol, "Movie", 2002)).GetProperty("id").GetInt32();
        var added = await admin.PostAsJsonAsync("/api/v1/movies",
            new { tmdbId = 2002, qualityProfileId = profile, rootFolderPath = root, searchForMovie = false }, Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Contains(other, await ListIdsAsync(carol, "approved"));
    }
}
