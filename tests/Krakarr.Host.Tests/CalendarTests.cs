using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

public sealed class CalendarTests : IClassFixture<KrakarrFactory>
{
    private readonly KrakarrFactory _factory;

    public CalendarTests(KrakarrFactory factory) => _factory = factory;

    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = _factory.CreateClient(new() { HandleCookies = true });
        var ct = TestContext.Current.CancellationToken;
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task SeedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        if (await db.Movies.AnyAsync(ct)) return;
        var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);

        var movie = new Movie { TmdbId = 1, Title = "Calendar Movie", Year = 2030, Path = "/m/cal", QualityProfileId = profile };
        MovieReleases.Apply(movie,
        [
            new("US", ReleaseDateType.Theatrical, new DateOnly(2030, 3, 7)),
            new("ES", ReleaseDateType.Theatrical, new DateOnly(2030, 3, 14)),
            new("US", ReleaseDateType.Digital, new DateOnly(2030, 4, 20)),
            new("US", ReleaseDateType.Physical, new DateOnly(2030, 6, 1)),
        ], MovieStatus.Announced, new DateOnly(2026, 1, 1));
        db.Movies.Add(movie);
        db.Movies.Add(new Movie { TmdbId = 2, Title = "Unmonitored Movie", Year = 2030, Path = "/m/unmon", QualityProfileId = profile, Monitored = false,
            InCinemas = new DateOnly(2030, 3, 10), ReleaseDates = [new("US", ReleaseDateType.Theatrical, new DateOnly(2030, 3, 10))] });

        var series = new Krakarr.Core.Domain.Series { TmdbId = 10, Title = "Calendar Show", Year = 2030, Path = "/tv/cal", QualityProfileId = profile, Network = "HBO" };
        series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, Title = "Pilot", AirDate = new DateOnly(2030, 3, 8) });
        series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 2, Title = "Later", AirDate = new DateOnly(2030, 5, 8) });
        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Lists_movie_releases_and_episodes_in_range()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync();
        var client = await LoggedInClientAsync();
        (await client.PutAsJsonAsync("/api/v1/settings/metadata", new { language = "en-US", releaseRegion = "" }, ct)).EnsureSuccessStatusCode();

        var entries = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/calendar?start=2030-03-01&end=2030-03-31", ct);
        Assert.Equal(
            ["2030-03-07 Movie Calendar Movie Theatrical US", "2030-03-08 Episode Calendar Show  ", "2030-03-10 Movie Unmonitored Movie Theatrical US"],
            entries!.Select(e => $"{e.GetProperty("date").GetString()} {e.GetProperty("kind").GetString()} {e.GetProperty("title").GetString()} {Str(e, "releaseType")} {Str(e, "country")}"));

        var monitoredOnly = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/calendar?start=2030-03-01&end=2030-03-31&unmonitored=false", ct);
        Assert.Equal(2, monitoredOnly!.Length);

        // With a region, its date wins and the earlier worldwide date is reported alongside.
        (await client.PutAsJsonAsync("/api/v1/settings/metadata", new { language = "en-US", releaseRegion = "es" }, ct)).EnsureSuccessStatusCode();
        var regional = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/calendar?start=2030-03-01&end=2030-06-30", ct) ?? [];
        var cinema = regional.Single(e => Str(e, "releaseType") == "Theatrical" && Str(e, "title") == "Calendar Movie");
        Assert.Equal("2030-03-14", cinema.GetProperty("date").GetString());
        Assert.True(cinema.GetProperty("isRegionDate").GetBoolean());
        Assert.Equal("2030-03-07", cinema.GetProperty("earliestDate").GetString());
        var digital = regional.Single(e => Str(e, "releaseType") == "Digital");
        Assert.False(digital.GetProperty("isRegionDate").GetBoolean());
        Assert.Equal("US", Str(digital, "country"));

        var bad = await client.PutAsJsonAsync("/api/v1/settings/metadata", new { language = "en-US", releaseRegion = "Spain" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (await client.PutAsJsonAsync("/api/v1/settings/metadata", new { language = "en-US" }, ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Rejects_invalid_ranges()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?start=2030-03-10&end=2030-03-01", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/calendar?start=2030-01-01&end=2032-01-01", ct)).StatusCode);
    }

    [Fact]
    public async Task Ics_feed_requires_auth_and_accepts_api_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();
        var key = (await client.GetFromJsonAsync<JsonElement>("/api/v1/settings/general", ct)).GetProperty("apiKey").GetString();

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/calendar/feed.ics", ct)).StatusCode);

        var res = await anonymous.GetAsync($"/api/v1/calendar/feed.ics?apikey={key}&releaseTypes=digital,cinema", ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", await res.Content.ReadAsStringAsync(ct));

        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync($"/api/v1/calendar/feed.ics?apikey={key}&releaseTypes=bogus", ct)).StatusCode);
    }

    [Fact]
    public void Writes_valid_icalendar()
    {
        var entries = new[]
        {
            new CalendarEntry { Kind = CalendarEntryKind.Movie, Date = new DateOnly(2030, 3, 7), ReleaseType = ReleaseDateType.Digital, Country = "US", MovieId = 5, Title = "Dune, Part; Two" },
            new CalendarEntry
            {
                Kind = CalendarEntryKind.Episode, Date = new DateOnly(2030, 3, 8), EpisodeId = 9, SeriesId = 2, Title = "Show", SeasonNumber = 1, EpisodeNumber = 2, EpisodeTitle = "Pilot",
                Overview = new string('x', 200),
            },
        };

        var ics = ICalendarWriter.Write(entries, "Krakarr", new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Contains("SUMMARY:Dune\\, Part\\; Two (Digital)\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("UID:movie-5-digital@krakarr\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20300307\r\nDTEND;VALUE=DATE:20300308\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Show - S01E02 - Pilot\r\n", ics, StringComparison.Ordinal);
        Assert.All(ics.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.EndsWith("END:VCALENDAR\r\n", ics, StringComparison.Ordinal);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : string.Empty;
}
