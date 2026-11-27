using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>The user's own alternate titles of movies and series.</summary>
public sealed class AlternateTitleTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
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
    public async Task User_titles_replace_only_the_users_own()
    {
        var ct = TestContext.Current.CancellationToken;
        int movieId, seriesId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var movie = new Movie { TmdbId = 80_001, Title = "Spirited Away", Year = 2001, Path = "/movies/spirited", QualityProfileId = profile };
            movie.AlternateTitles.Add(new AlternateTitle { Title = "El viaje de Chihiro", NormalizedTitle = "elviajedechihiro", Language = "es", Source = AlternateTitleSource.Metadata });
            var series = new Core.Domain.Series { TmdbId = 80_002, Title = "The Office", MetadataTitle = "The Office", Path = "/tv/office", QualityProfileId = profile };
            db.Movies.Add(movie);
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            (movieId, seriesId) = (movie.Id, series.Id);
        }

        var client = await AdminAsync(ct);
        var put = await client.PutAsJsonAsync($"/api/v1/movies/{movieId}/titles",
            new { titles = new object[] { new { title = "Chihiro", language = "es" }, new { title = "Spirited Away" }, new { title = " " } } }, ct);
        put.EnsureSuccessStatusCode();

        var titles = await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies/{movieId}/titles", ct);
        Assert.Equal(
            ["Chihiro|es|User", "El viaje de Chihiro|es|Metadata"],
            titles.EnumerateArray().Select(t => $"{t.GetProperty("title").GetString()}|{t.GetProperty("language").GetString()}|{t.GetProperty("source").GetString()}"));

        (await client.PutAsJsonAsync($"/api/v1/movies/{movieId}/titles", new { titles = Array.Empty<object>() }, ct)).EnsureSuccessStatusCode();
        Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies/{movieId}/titles", ct)).EnumerateArray());

        (await client.PutAsJsonAsync($"/api/v1/series/{seriesId}/titles", new { titles = new[] { new { title = "La Oficina (2005)", language = "es" } } }, ct)).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().AlternateTitles.AsNoTracking().SingleAsync(t => t.SeriesId == seriesId, ct);
            Assert.Equal("laoficina", stored.NormalizedTitle); // series titles drop the trailing year, like the main title
        }

        var tooLong = await client.PutAsJsonAsync($"/api/v1/series/{seriesId}/titles", new { titles = new[] { new { title = new string('x', 201) } } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/series/999999/titles", ct)).StatusCode);
    }
}
