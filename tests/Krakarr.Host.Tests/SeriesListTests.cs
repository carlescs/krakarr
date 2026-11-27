using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>The series list aggregates its counts in SQL; they must match the detail endpoint, which loads the episodes.</summary>
public sealed class SeriesListTests : IClassFixture<KrakarrFactory>
{
    private readonly KrakarrFactory _factory;

    public SeriesListTests(KrakarrFactory factory) => _factory = factory;

    [Fact]
    public async Task Lists_series_with_episode_counts_and_sizes()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var series = new Krakarr.Core.Domain.Series { TmdbId = 77, Title = "Counted Show", Year = 2020, Path = "/tv/counted", QualityProfileId = profile };
            series.Seasons.Add(new Season { SeasonNumber = 0, Monitored = false });
            series.Seasons.Add(new Season { SeasonNumber = 1, Monitored = true });
            series.Seasons.Add(new Season { SeasonNumber = 2, Monitored = true });
            db.Series.Add(series);
            db.Series.Add(new Krakarr.Core.Domain.Series { TmdbId = 78, Title = "Empty Show", Year = 2021, Path = "/tv/empty", QualityProfileId = profile });
            await db.SaveChangesAsync(ct);

            // One double-episode file (counted once in the size), one special, an aired episode without a file and a future one.
            var pack = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "S01E01E02.mkv", Size = 100 };
            var special = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 0, RelativePath = "S00E01.mkv", Size = 5 };
            series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2020, 1, 1), EpisodeFile = pack });
            series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2020, 1, 8), EpisodeFile = pack });
            series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 3, AirDate = new DateOnly(2020, 1, 15) });
            series.Episodes.Add(new Episode { SeasonNumber = 2, EpisodeNumber = 1, AirDate = new DateOnly(2099, 1, 1) });
            series.Episodes.Add(new Episode { SeasonNumber = 0, EpisodeNumber = 1, AirDate = new DateOnly(2020, 2, 1), EpisodeFile = special });
            await db.SaveChangesAsync(ct);
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/series", ct);
        var list = page.GetProperty("items").EnumerateArray().ToArray();
        var show = list.Single(s => s.GetProperty("title").GetString() == "Counted Show");
        Assert.Equal(3, show.GetProperty("episodeCount").GetInt32());
        Assert.Equal(2, show.GetProperty("episodeFileCount").GetInt32());
        Assert.Equal("2099-01-01", show.GetProperty("nextAiring").GetString());
        Assert.Equal(0, show.GetProperty("downloadingCount").GetInt32());
        Assert.False(show.TryGetProperty("seasons", out _), "the list must stay slim");

        var empty = list.Single(s => s.GetProperty("title").GetString() == "Empty Show");
        Assert.Equal(0, empty.GetProperty("episodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextAiring").ValueKind);

        // The single-series endpoint (loaded entities) must agree with the aggregated list.
        var single = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{show.GetProperty("id").GetInt32()}", ct);
        Assert.Equal(3, single.GetProperty("episodeCount").GetInt32());
        Assert.Equal(2, single.GetProperty("episodeFileCount").GetInt32());
        Assert.Equal(105, single.GetProperty("sizeOnDisk").GetInt64());

        var seasons = single.GetProperty("seasons").EnumerateArray()
            .Select(s => $"{s.GetProperty("seasonNumber").GetInt32()}:{s.GetProperty("episodeCount").GetInt32()}/{s.GetProperty("episodeFileCount").GetInt32()}/{s.GetProperty("totalEpisodeCount").GetInt32()}/{s.GetProperty("sizeOnDisk").GetInt64()}");
        Assert.Equal(["0:1/1/1/5", "1:3/2/3/100", "2:0/0/1/0"], seasons);
    }

    [Fact]
    public async Task Paginates_filters_sorts_and_lists_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        int darkId, lostId, severanceId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            db.Series.RemoveRange(db.Series);
            await db.SaveChangesAsync(ct);

            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var dark = new Krakarr.Core.Domain.Series { TmdbId = 81, Title = "Dark", SortTitle = "dark", Year = 2017, Network = "Netflix", Status = SeriesStatus.Ended, Path = "/tv/dark", QualityProfileId = profile };
            var lost = new Krakarr.Core.Domain.Series { TmdbId = 82, Title = "Lost", SortTitle = "lost", Year = 2004, Status = SeriesStatus.Ended, Monitored = false, Path = "/tv/lost", QualityProfileId = profile };
            var severance = new Krakarr.Core.Domain.Series { TmdbId = 83, Title = "Severance", SortTitle = "severance", Year = 2022, Network = "Apple TV+", Status = SeriesStatus.Continuing, Path = "/tv/severance", QualityProfileId = profile };
            db.Series.AddRange(dark, lost, severance);
            await db.SaveChangesAsync(ct);

            var darkFile = new EpisodeFile { SeriesId = dark.Id, SeasonNumber = 1, RelativePath = "S01E01.mkv", Size = 10 };
            dark.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2017, 12, 1), EpisodeFile = darkFile });

            var sevFile = new EpisodeFile { SeriesId = severance.Id, SeasonNumber = 1, RelativePath = "S01E01.mkv", Size = 10 };
            severance.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2022, 2, 18), EpisodeFile = sevFile });
            severance.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2022, 2, 25) });
            severance.Episodes.Add(new Episode { SeasonNumber = 2, EpisodeNumber = 1, AirDate = new DateOnly(2099, 5, 1) });

            lost.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2004, 9, 22) });
            await db.SaveChangesAsync(ct);
            (darkId, lostId, severanceId) = (dark.Id, lost.Id, severance.Id);
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/v1/subscriptions/Series/{darkId}", new { }, ct)).EnsureSuccessStatusCode();

        // Pagination
        var p1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?page=1&pageSize=2", ct);
        Assert.Equal(3, p1.GetProperty("total").GetInt32());
        Assert.Equal(3, p1.GetProperty("libraryTotal").GetInt32());
        Assert.Equal(["Dark", "Lost"], p1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var p2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?page=2&pageSize=2", ct);
        Assert.Equal(["Severance"], p2.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // Filters & search
        var missing = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?filter=missing", ct);
        Assert.Equal(1, missing.GetProperty("total").GetInt32());
        Assert.Equal(3, missing.GetProperty("libraryTotal").GetInt32());
        Assert.Equal(["Severance"], missing.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var ended = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?filter=ended", ct);
        Assert.Equal(["Dark", "Lost"], ended.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var following = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?filter=following", ct);
        Assert.Equal(["Dark"], following.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var search = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?q=lo", ct);
        Assert.Equal(["Lost"], search.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // The filter also matches the network, which the list returns.
        var byNetworkName = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?q=netflix", ct);
        var netflix = Assert.Single(byNetworkName.GetProperty("items").EnumerateArray());
        Assert.Equal("Dark", netflix.GetProperty("title").GetString());
        Assert.Equal("Netflix", netflix.GetProperty("network").GetString());

        // Sorting (including episode completion ratio and nextAiring with nulls last)
        var byEpisodes = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?sort=episodes", ct);
        Assert.Equal(["Lost", "Severance", "Dark"], byEpisodes.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var byNextAiring = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?sort=nextAiring", ct);
        Assert.Equal(["Severance", "Dark", "Lost"], byNextAiring.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // Network: shows without one go last in either direction.
        var byNetwork = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?sort=network", ct);
        Assert.Equal(["Severance", "Dark", "Lost"], byNetwork.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        var byNetworkDesc = await client.GetFromJsonAsync<JsonElement>("/api/v1/series?sort=network&desc=true", ct);
        Assert.Equal(["Dark", "Severance", "Lost"], byNetworkDesc.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // Matching ids
        var ids = await client.GetFromJsonAsync<int[]>("/api/v1/series/ids?sort=episodes", ct) ?? [];
        Assert.Equal([lostId, severanceId, darkId], ids);
    }

    [Fact]
    public async Task Title_can_be_edited_and_reset()
    {
        var ct = TestContext.Current.CancellationToken;
        int id, profile;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var series = new Krakarr.Core.Domain.Series { TmdbId = 79, Title = "Same Name", MetadataTitle = "Same Name", Year = 2011, Path = "/tv/same", QualityProfileId = profile };
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            id = series.Id;
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync($"/api/v1/series/{id}", new { monitored = true, qualityProfileId = profile, seasonFolder = true, title = "Same Name (2011)" }, ct)).EnsureSuccessStatusCode();
        var edited = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{id}", ct);
        Assert.Equal("Same Name (2011)", edited.GetProperty("title").GetString());
        Assert.Equal("Same Name", edited.GetProperty("metadataTitle").GetString());

        // Leaving the title out keeps it.
        (await client.PutAsJsonAsync($"/api/v1/series/{id}", new { monitored = false, qualityProfileId = profile, seasonFolder = true }, ct)).EnsureSuccessStatusCode();
        Assert.Equal("Same Name (2011)", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{id}", ct)).GetProperty("title").GetString());

        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/v1/series/{id}", new { monitored = true, qualityProfileId = profile, seasonFolder = true, title = new string('x', 201) }, ct)).StatusCode);

        // A blank title restores the metadata title.
        (await client.PutAsJsonAsync($"/api/v1/series/{id}", new { monitored = true, qualityProfileId = profile, seasonFolder = true, title = "" }, ct)).EnsureSuccessStatusCode();
        Assert.Equal("Same Name", (await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{id}", ct)).GetProperty("title").GetString());
    }
}
