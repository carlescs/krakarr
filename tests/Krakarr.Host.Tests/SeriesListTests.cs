using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>The series list builds its counts from projected episode columns, not loaded episodes.</summary>
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
            var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
            var series = new Krakarr.Core.Domain.Series { TmdbId = 77, Title = "Counted Show", Year = 2020, Path = "/tv/counted", QualityProfileId = profile };
            series.Seasons.Add(new Season { SeasonNumber = 0, Monitored = false });
            series.Seasons.Add(new Season { SeasonNumber = 1, Monitored = true });
            series.Seasons.Add(new Season { SeasonNumber = 2, Monitored = true });
            db.Series.Add(series);
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

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/series", ct);
        var show = list!.Single(s => s.GetProperty("title").GetString() == "Counted Show");
        Assert.Equal(3, show.GetProperty("episodeCount").GetInt32());
        Assert.Equal(2, show.GetProperty("episodeFileCount").GetInt32());
        Assert.Equal(105, show.GetProperty("sizeOnDisk").GetInt64());
        Assert.Equal("2099-01-01", show.GetProperty("nextAiring").GetString());

        var seasons = show.GetProperty("seasons").EnumerateArray()
            .Select(s => $"{s.GetProperty("seasonNumber").GetInt32()}:{s.GetProperty("episodeCount").GetInt32()}/{s.GetProperty("episodeFileCount").GetInt32()}/{s.GetProperty("totalEpisodeCount").GetInt32()}/{s.GetProperty("sizeOnDisk").GetInt64()}");
        Assert.Equal(["0:1/1/1/5", "1:3/2/3/100", "2:0/0/1/0"], seasons);

        // The single-series endpoint (loaded entities) must agree with the projected list.
        var single = await client.GetFromJsonAsync<JsonElement>($"/api/v1/series/{show.GetProperty("id").GetInt32()}", ct);
        Assert.Equal(105, single.GetProperty("sizeOnDisk").GetInt64());
        Assert.Equal(3, single.GetProperty("episodeCount").GetInt32());
    }
}
