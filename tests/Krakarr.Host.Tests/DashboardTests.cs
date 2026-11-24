using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class DashboardTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> LoginAsync(string username = "admin", string password = "correct-horse")
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        var res = await client.PostAsJsonAsync(url, new { username, password, setupToken = KrakarrFactory.TestSetupToken }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task Summarises_library_users_requests_and_activity()
    {
        var admin = await LoginAsync();
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/users", new { username = "dora", password = "password-123", role = "User" }, Ct)).StatusCode);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = db.QualityProfiles.Min(p => p.Id);
            var dora = db.Users.Single(u => u.Username == "dora");

            var have = new Movie { TmdbId = 1, Title = "Alien", Year = 1979, Path = "/m/alien", QualityProfileId = profile };
            var want = new Movie { TmdbId = 2, Title = "Aliens", Year = 1986, Path = "/m/aliens", QualityProfileId = profile };
            var skip = new Movie { TmdbId = 3, Title = "Alien 3", Year = 1992, Path = "/m/alien3", QualityProfileId = profile, Monitored = false };
            db.Movies.AddRange(have, want, skip);
            var show = new Core.Domain.Series { TmdbId = 10, Title = "Severance", Year = 2022, Path = "/s/sev", QualityProfileId = profile };
            db.Series.Add(show);
            await db.SaveChangesAsync(Ct);

            var movieFile = new MovieFile { MovieId = have.Id, RelativePath = "a.mkv", Size = 4_000, QualityId = Qualities.Bluray1080.Id };
            db.MovieFiles.Add(movieFile);
            var episodeFile = new EpisodeFile { SeriesId = show.Id, SeasonNumber = 1, RelativePath = "e1.mkv", Size = 1_000, QualityId = Qualities.WebDl2160.Id };
            db.EpisodeFiles.Add(episodeFile);
            await db.SaveChangesAsync(Ct);
            have.MovieFileId = movieFile.Id;
            db.Episodes.AddRange(
                new Episode { SeriesId = show.Id, SeasonNumber = 1, EpisodeNumber = 1, AirDate = today.AddDays(-30), EpisodeFileId = episodeFile.Id },
                new Episode { SeriesId = show.Id, SeasonNumber = 1, EpisodeNumber = 2, AirDate = today.AddDays(-23) },
                new Episode { SeriesId = show.Id, SeasonNumber = 1, EpisodeNumber = 3, AirDate = today.AddDays(3) },
                new Episode { SeriesId = show.Id, SeasonNumber = 0, EpisodeNumber = 1, AirDate = today.AddDays(-40) });

            db.MediaRequests.AddRange(
                new MediaRequest { MediaType = MediaType.Movie, TmdbId = 50, Title = "Old", RequestedById = dora.Id, Status = RequestStatus.Approved, RequestedAt = DateTimeOffset.UtcNow.AddDays(-60) },
                new MediaRequest { MediaType = MediaType.Series, TmdbId = 51, Title = "New", RequestedById = dora.Id, RequestedAt = DateTimeOffset.UtcNow.AddDays(-1) });

            db.History.AddRange(
                new HistoryEntry { EventType = HistoryEventType.Grabbed, SourceTitle = "Alien.1979", MovieId = have.Id },
                new HistoryEntry { EventType = HistoryEventType.Imported, SourceTitle = "Alien.1979", MovieId = have.Id, Date = DateTimeOffset.UtcNow.AddMinutes(-2) },
                new HistoryEntry { EventType = HistoryEventType.Imported, SourceTitle = "Severance.S01E01", SeriesId = show.Id, Date = DateTimeOffset.UtcNow.AddMinutes(-1) },
                new HistoryEntry { EventType = HistoryEventType.Imported, SourceTitle = "Severance.S01E01.Proper", SeriesId = show.Id },
                new HistoryEntry { EventType = HistoryEventType.DownloadFailed, SourceTitle = "Old.Failure", Date = DateTimeOffset.UtcNow.AddDays(-30) });
            await db.SaveChangesAsync(Ct);
        }

        var dash = await admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", Ct);

        var library = dash.GetProperty("library");
        var movies = library.GetProperty("movies");
        Assert.Equal(3, movies.GetProperty("total").GetInt32());
        Assert.Equal(1, movies.GetProperty("downloaded").GetInt32());
        Assert.Equal(1, movies.GetProperty("missing").GetInt32());
        var episodes = library.GetProperty("episodes");
        Assert.Equal(3, episodes.GetProperty("total").GetInt32()); // specials excluded
        Assert.Equal(1, episodes.GetProperty("downloaded").GetInt32());
        Assert.Equal(1, episodes.GetProperty("missing").GetInt32());
        Assert.Equal(1, episodes.GetProperty("airingThisWeek").GetInt32());
        Assert.Equal(1, library.GetProperty("series").GetProperty("continuing").GetInt32());
        Assert.Equal(5_000, library.GetProperty("sizeOnDisk").GetInt64());
        Assert.Equal([2160, 1080], library.GetProperty("resolutions").EnumerateArray().Select(r => r.GetProperty("resolution").GetInt32()));

        var users = dash.GetProperty("users");
        Assert.Equal(2, users.GetProperty("total").GetInt32());
        Assert.Equal(1, users.GetProperty("admins").GetInt32());
        var top = users.GetProperty("topRequesters")[0];
        Assert.Equal("dora", top.GetProperty("username").GetString());
        Assert.Equal(2, top.GetProperty("requests").GetInt32());

        var requests = dash.GetProperty("requests");
        Assert.Equal(1, requests.GetProperty("pending").GetInt32());
        Assert.Equal(1, requests.GetProperty("approved").GetInt32());
        Assert.Equal(1, requests.GetProperty("last30Days").GetInt32());
        Assert.Equal("New", requests.GetProperty("recent")[0].GetProperty("title").GetString());
        Assert.Equal("dora", requests.GetProperty("recent")[0].GetProperty("requestedBy").GetString());

        var activity = dash.GetProperty("activity");
        Assert.Equal(1, activity.GetProperty("grabbed").GetInt32());
        Assert.Equal(3, activity.GetProperty("imported").GetInt32());
        Assert.Equal(0, activity.GetProperty("failed").GetInt32()); // older than a week

        // One entry per title, newest first.
        var recent = dash.GetProperty("recentlyImported").EnumerateArray().Select(r => r.GetProperty("title").GetString()).ToArray();
        Assert.Equal(["Severance", "Alien"], recent);

        // Staff only.
        var doraClient = await LoginAsync("dora", "password-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await doraClient.GetAsync("/api/v1/dashboard", Ct)).StatusCode);
    }
}
