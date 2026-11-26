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
        public Task<DiscoverPage<SeriesLookup>> KeywordsAsync(IReadOnlyList<int> keywordIds, int page, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SeriesLookup>> RecommendationsAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SeriesLookup>> SimilarAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<IReadOnlyList<SeriesLookup>>> RecommendationsAsync(IReadOnlyList<SeriesIds> shows, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Credits> CreditsAsync(SeriesIds ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EpisodeDetails?> EpisodeDetailsAsync(SeriesIds ids, int season, int episode, CancellationToken ct = default) => throw new NotSupportedException();
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

    private static async Task<JsonElement?> BellAsync(HttpClient client, string id)
    {
        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/notifications", Ct);
        return all!.Select(n => (JsonElement?)n).SingleOrDefault(n => n!.Value.GetProperty("id").GetString() == id);
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

        // The bell: users count their own pending requests, staff the whole approval queue.
        var aliceBell = (await alice.GetFromJsonAsync<JsonElement[]>("/api/v1/notifications", Ct))!.Single(n => n.GetProperty("id").GetString() == "requests.mine");
        Assert.Equal(1, aliceBell.GetProperty("count").GetInt32());
        Assert.Equal("/requests", aliceBell.GetProperty("link").GetString());
        var staffBell = (await manager.GetFromJsonAsync<JsonElement[]>("/api/v1/notifications", Ct))!.Single(n => n.GetProperty("id").GetString() == "requests.pending");
        Assert.True(staffBell.GetProperty("count").GetInt32() >= 2);

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

        // Alice hears about the decline until she opens her requests; bob, whose request is untouched, doesn't.
        var declinedBell = (await BellAsync(alice, "requests.declined"))!.Value;
        Assert.Equal(1, declinedBell.GetProperty("count").GetInt32());
        Assert.Equal("Your request was declined", declinedBell.GetProperty("title").GetString());
        Assert.Equal("Movie 1001", declinedBell.GetProperty("message").GetString());
        Assert.Null(await BellAsync(bob, "requests.declined"));
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync("/api/v1/requests/seen", null, Ct)).StatusCode);
        Assert.Null(await BellAsync(alice, "requests.declined"));

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
            profile = await db.QualityProfiles.MinAsync(p => p.Id, Ct);
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
        var approvedBell = (await BellAsync(carol, "requests.approved"))!.Value;
        Assert.Equal("Your request was approved", approvedBell.GetProperty("title").GetString());
        Assert.Equal("Movie 2001", approvedBell.GetProperty("message").GetString());

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

        // Requests fulfilled by adding the title directly count as approved too.
        approvedBell = (await BellAsync(carol, "requests.approved"))!.Value;
        Assert.Equal(2, approvedBell.GetProperty("count").GetInt32());
        Assert.Equal("2 of your requests were approved", approvedBell.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await carol.PostAsync("/api/v1/requests/seen", null, Ct)).StatusCode);
        Assert.Null(await BellAsync(carol, "requests.approved"));

        // Both titles are in carol's subscriptions now, so they show in her "My titles".
        var subscriptions = await carol.GetFromJsonAsync<JsonElement[]>("/api/v1/subscriptions", Ct);
        var followed = subscriptions!.Where(s => s.GetProperty("mediaType").GetString() == "Movie").Select(s => s.GetProperty("libraryId").GetInt32()).ToArray();
        Assert.Contains(movieId, followed);
        Assert.Equal(2, followed.Length);

        // Whoever adds a title follows it; approving someone else's request doesn't.
        var adminFollows = (await admin.GetFromJsonAsync<JsonElement[]>("/api/v1/subscriptions", Ct))!
            .Select(x => x.GetProperty("libraryId").GetInt32()).ToArray();
        var addedId = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        Assert.Contains(addedId, adminFollows);
        Assert.DoesNotContain(movieId, adminFollows);
    }

    [Fact]
    public async Task Users_subscribe_to_library_titles_and_see_them_in_their_calendar()
    {
        var dave = await UserAsync("dave");
        var erin = await UserAsync("erin");
        var manager = await UserAsync("max", "Manager");
        int movieId, otherMovieId, seriesId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, Ct);
            var movie = new Movie { TmdbId = 5001, Title = "Followed Movie", Year = 2031, Path = "/m/followed", QualityProfileId = profile,
                InCinemas = new DateOnly(2031, 2, 3), ReleaseDates = [new("US", ReleaseDateType.Theatrical, new DateOnly(2031, 2, 3))] };
            var other = new Movie { TmdbId = 5002, Title = "Other Movie", Year = 2031, Path = "/m/other", QualityProfileId = profile,
                InCinemas = new DateOnly(2031, 2, 4), ReleaseDates = [new("US", ReleaseDateType.Theatrical, new DateOnly(2031, 2, 4))] };
            var show = new Krakarr.Core.Domain.Series { TmdbId = 5003, Title = "Followed Show", Year = 2031, Path = "/tv/followed", QualityProfileId = profile };
            show.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot", AirDate = new DateOnly(2031, 2, 5) });
            db.AddRange(movie, other, show);
            await db.SaveChangesAsync(Ct);
            (movieId, otherMovieId, seriesId) = (movie.Id, other.Id, show.Id);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await dave.PutAsync($"/api/v1/subscriptions/Movie/{movieId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await dave.PutAsync($"/api/v1/subscriptions/Movie/{movieId}", null, Ct)).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.NoContent, (await dave.PutAsync($"/api/v1/subscriptions/Series/{seriesId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await erin.PutAsync($"/api/v1/subscriptions/Movie/{otherMovieId}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await dave.PutAsync("/api/v1/subscriptions/Movie/999999", null, Ct)).StatusCode);

        var state = await dave.GetFromJsonAsync<JsonElement>($"/api/v1/subscriptions/Movie/{movieId}", Ct);
        Assert.True(state.GetProperty("subscribed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("subscribers").ValueKind); // names are for staff
        var staffState = await manager.GetFromJsonAsync<JsonElement>($"/api/v1/subscriptions/Movie/{movieId}", Ct);
        Assert.False(staffState.GetProperty("subscribed").GetBoolean());
        Assert.Equal(["dave"], staffState.GetProperty("subscribers").EnumerateArray().Select(x => x.GetString()));

        // "My titles" calendar: only what the caller follows.
        const string range = "/api/v1/calendar?start=2031-02-01&end=2031-02-28";
        var mine = await dave.GetFromJsonAsync<JsonElement[]>(range + "&user=me", Ct);
        Assert.Equal(["Followed Movie", "Followed Show"], mine!.Select(e => e.GetProperty("title").GetString()));
        var everything = await dave.GetFromJsonAsync<JsonElement[]>(range, Ct);
        Assert.Equal(3, everything!.Length);

        // Staff can look at anyone's titles; plain users only at their own.
        var erinId = (await manager.GetFromJsonAsync<JsonElement[]>("/api/v1/subscriptions/users", Ct))!
            .Single(u => u.GetProperty("username").GetString() == "erin").GetProperty("id").GetInt32();
        var erins = await manager.GetFromJsonAsync<JsonElement[]>(range + $"&user={erinId}", Ct);
        Assert.Equal(["Other Movie"], erins!.Select(e => e.GetProperty("title").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await dave.GetAsync(range + $"&user={erinId}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await dave.GetAsync("/api/v1/subscriptions/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await dave.GetAsync(range + "&user=someone", Ct)).StatusCode);

        // Unsubscribing, and removing a title, drop the subscription.
        Assert.Equal(HttpStatusCode.NoContent, (await dave.DeleteAsync($"/api/v1/subscriptions/Series/{seriesId}", Ct)).StatusCode);
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/movies/{movieId}", Ct)).StatusCode);
        Assert.Empty((await dave.GetFromJsonAsync<JsonElement[]>("/api/v1/subscriptions", Ct))!);
        Assert.Empty((await dave.GetFromJsonAsync<JsonElement[]>(range + "&user=me", Ct))!);
    }

    [Fact]
    public async Task Notifications_can_be_dismissed_and_reappear_on_new_activity()
    {
        var frank = await UserAsync("frank");
        var grace = await UserAsync("grace");
        var manager = await UserAsync("maya", "Manager");

        var r1 = (await CreateAsync(frank, "Movie", 3001)).GetProperty("id").GetInt32();
        var r2 = (await CreateAsync(frank, "Movie", 3002)).GetProperty("id").GetInt32();
        Assert.Equal(2, (await BellAsync(frank, "requests.mine"))!.Value.GetProperty("count").GetInt32());

        // Dismissing hides the notification for that user only, until a newer request arrives.
        Assert.Equal(HttpStatusCode.NoContent, (await frank.DeleteAsync("/api/v1/notifications/requests.mine", Ct)).StatusCode);
        Assert.Null(await BellAsync(frank, "requests.mine"));
        Assert.NotNull(await BellAsync(manager, "requests.pending"));

        var r3 = (await CreateAsync(frank, "Movie", 3003)).GetProperty("id").GetInt32();
        Assert.Equal(3, (await BellAsync(frank, "requests.mine"))!.Value.GetProperty("count").GetInt32());

        // Dismissing a result notification clears the handled requests so the next decline only counts the new one.
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/v1/requests/{r1}/decline", new { }, Ct)).StatusCode);
        Assert.Equal(1, (await BellAsync(frank, "requests.declined"))!.Value.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await frank.DeleteAsync("/api/v1/notifications/requests.declined", Ct)).StatusCode);
        Assert.Null(await BellAsync(frank, "requests.declined"));

        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/v1/requests/{r2}/decline", new { }, Ct)).StatusCode);
        var declined = (await BellAsync(frank, "requests.declined"))!.Value;
        Assert.Equal(1, declined.GetProperty("count").GetInt32());
        Assert.Equal("Movie 3002", declined.GetProperty("message").GetString());
        Assert.Equal("bell", declined.GetProperty("tray").GetString());

        // Dismissing another tray leaves the bell alone, request results included.
        Assert.Equal(HttpStatusCode.NoContent, (await frank.DeleteAsync("/api/v1/notifications?tray=subtitleSync", Ct)).StatusCode);
        Assert.NotNull(await BellAsync(frank, "requests.declined"));
        Assert.NotNull(await BellAsync(frank, "requests.mine"));

        // Dismiss all clears every notification in the caller's bell without touching other users'.
        await CreateAsync(grace, "Movie", 3004);
        Assert.Equal(HttpStatusCode.NoContent, (await frank.DeleteAsync("/api/v1/notifications", Ct)).StatusCode);
        Assert.Empty((await frank.GetFromJsonAsync<JsonElement[]>("/api/v1/notifications", Ct))!);
        Assert.NotNull(await BellAsync(grace, "requests.mine"));

        Assert.Equal(HttpStatusCode.NoContent, (await manager.DeleteAsync($"/api/v1/requests/{r3}", Ct)).StatusCode);
    }
}
