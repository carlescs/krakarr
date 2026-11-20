using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>The movie list returns slim grid rows, projected in SQL.</summary>
public sealed class MovieListTests : IClassFixture<KrakarrFactory>
{
    private readonly KrakarrFactory _factory;

    public MovieListTests(KrakarrFactory factory) => _factory = factory;

    [Fact]
    public async Task Lists_movies_as_slim_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
            var ownedMovie = new Movie { TmdbId = 9001, Title = "Owned Movie", SortTitle = "owned movie", Year = 2001, Path = "/movies/owned", QualityProfileId = profile, Overview = "long text" };
            var missingMovie = new Movie { TmdbId = 9002, Title = "Missing Movie", SortTitle = "missing movie", Year = 2002, Path = "/movies/missing", QualityProfileId = profile, Monitored = false };
            db.Movies.AddRange(ownedMovie, missingMovie);
            await db.SaveChangesAsync(ct);
            ownedMovie.MovieFile = new MovieFile { MovieId = ownedMovie.Id, RelativePath = "owned.mkv", QualityId = 17 };
            await db.SaveChangesAsync(ct);
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/movies", ct);
        var titles = list!.Select(m => m.GetProperty("title").GetString()).ToList();
        Assert.True(titles.IndexOf("Missing Movie") < titles.IndexOf("Owned Movie"), "sorted by sort title");

        var owned = list!.Single(m => m.GetProperty("title").GetString() == "Owned Movie");
        Assert.True(owned.GetProperty("hasFile").GetBoolean());
        Assert.Equal(Qualities.FindById(17).Name, owned.GetProperty("quality").GetString());
        Assert.Equal(2001, owned.GetProperty("year").GetInt32());
        Assert.False(owned.TryGetProperty("overview", out _), "the list must stay slim");

        var missing = list!.Single(m => m.GetProperty("title").GetString() == "Missing Movie");
        Assert.False(missing.GetProperty("hasFile").GetBoolean());
        Assert.False(missing.GetProperty("monitored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("quality").ValueKind);
    }
}
