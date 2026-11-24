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
            db.Movies.RemoveRange(db.Movies);
            await db.SaveChangesAsync(ct);
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
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

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies", ct);
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("libraryTotal").GetInt32());
        var list = page.GetProperty("items").EnumerateArray().ToArray();
        var titles = list.Select(m => m.GetProperty("title").GetString()).ToList();
        Assert.True(titles.IndexOf("Missing Movie") < titles.IndexOf("Owned Movie"), "sorted by sort title");

        var owned = list.Single(m => m.GetProperty("title").GetString() == "Owned Movie");
        Assert.True(owned.GetProperty("hasFile").GetBoolean());
        Assert.Equal(Qualities.FindById(17).Name, owned.GetProperty("quality").GetString());
        Assert.Equal(2001, owned.GetProperty("year").GetInt32());
        Assert.False(owned.TryGetProperty("overview", out _), "the list must stay slim");
        Assert.Equal(JsonValueKind.Null, owned.GetProperty("collectionId").ValueKind);

        var missing = list.Single(m => m.GetProperty("title").GetString() == "Missing Movie");
        Assert.False(missing.GetProperty("hasFile").GetBoolean());
        Assert.False(missing.GetProperty("monitored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("quality").ValueKind);
    }

    [Fact]
    public async Task Paginates_filters_sorts_groups_and_lists_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        int alienId, aliensId, arrivalId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            db.Movies.RemoveRange(db.Movies);
            db.MovieCollections.RemoveRange(db.MovieCollections);
            await db.SaveChangesAsync(ct);

            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var col = new MovieCollection { TmdbId = 8091, Name = "Alien Collection", SortName = "alien collection" };
            db.MovieCollections.Add(col);
            await db.SaveChangesAsync(ct);

            var alien = new Movie { TmdbId = 9101, Title = "Alien", SortTitle = "alien", Year = 1979, Path = "/movies/alien", QualityProfileId = profile, CollectionId = col.Id };
            var aliens = new Movie { TmdbId = 9102, Title = "Aliens", SortTitle = "aliens", Year = 1986, Path = "/movies/aliens", QualityProfileId = profile, CollectionId = col.Id };
            var arrival = new Movie { TmdbId = 9103, Title = "Arrival", SortTitle = "arrival", Year = 2016, Path = "/movies/arrival", QualityProfileId = profile, Monitored = false };
            db.Movies.AddRange(alien, aliens, arrival);
            await db.SaveChangesAsync(ct);
            alien.MovieFile = new MovieFile { MovieId = alien.Id, RelativePath = "alien.mkv", QualityId = 17 };
            await db.SaveChangesAsync(ct);
            (alienId, aliensId, arrivalId) = (alien.Id, aliens.Id, arrival.Id);
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        (await client.PutAsJsonAsync($"/api/v1/subscriptions/Movie/{arrivalId}", new { }, ct)).EnsureSuccessStatusCode();

        // Pagination
        var p1 = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?page=1&pageSize=2", ct);
        Assert.Equal(3, p1.GetProperty("total").GetInt32());
        Assert.Equal(3, p1.GetProperty("libraryTotal").GetInt32());
        Assert.Equal(["Alien", "Aliens"], p1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var p2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?page=2&pageSize=2", ct);
        Assert.Equal(["Arrival"], p2.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var p3 = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?page=3&pageSize=2", ct);
        Assert.Empty(p3.GetProperty("items").EnumerateArray());

        // Filters & search (including collection name)
        var missing = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?filter=missing", ct);
        Assert.Equal(1, missing.GetProperty("total").GetInt32());
        Assert.Equal(3, missing.GetProperty("libraryTotal").GetInt32());
        Assert.Equal(["Aliens"], missing.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var following = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?filter=following", ct);
        Assert.Equal(["Arrival"], following.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var byCollectionName = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?q=alien%20collection", ct);
        Assert.Equal(["Alien", "Aliens"], byCollectionName.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // Sorting
        var byYearDesc = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?sort=year&desc=true", ct);
        Assert.Equal(["Arrival", "Aliens", "Alien"], byYearDesc.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        var byStatus = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?sort=status", ct);
        Assert.Equal(["Aliens", "Arrival", "Alien"], byStatus.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));

        // Grouping by collection
        var grouped = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?groupBy=collection&page=1&pageSize=2", ct);
        Assert.Equal(["Alien", "Aliens"], grouped.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        var groups = grouped.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("Alien Collection", groups[0].GetProperty("title").GetString());
        Assert.Equal(2, groups[0].GetProperty("count").GetInt32());
        Assert.Equal(1, groups[0].GetProperty("downloaded").GetInt32());
        Assert.Equal("other", groups[1].GetProperty("key").GetString());
        Assert.Equal(1, groups[1].GetProperty("count").GetInt32());

        // Only the first page carries the groups: later pages would recompute them for nothing.
        var groupedPage2 = await client.GetFromJsonAsync<JsonElement>("/api/v1/movies?groupBy=collection&page=2&pageSize=2", ct);
        Assert.Equal(["Arrival"], groupedPage2.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        Assert.Equal(JsonValueKind.Null, groupedPage2.GetProperty("groups").ValueKind);

        // Matching ids
        var ids = await client.GetFromJsonAsync<int[]>("/api/v1/movies/ids?sort=year&desc=true", ct) ?? [];
        Assert.Equal([arrivalId, aliensId, alienId], ids);
    }

    [Fact]
    public async Task Search_ignores_case_of_non_ascii_letters()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            db.Movies.RemoveRange(db.Movies);
            await db.SaveChangesAsync(ct);
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            db.Movies.AddRange(
                new Movie { TmdbId = 9201, Title = "Àlex i l'Ésser", SortTitle = "alex i l'esser", Year = 2020, Path = "/movies/alex", QualityProfileId = profile },
                new Movie { TmdbId = 9202, Title = "Other", SortTitle = "other", Year = 2021, Path = "/movies/other", QualityProfileId = profile });
            await db.SaveChangesAsync(ct);
        }

        var client = _factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();

        foreach (var q in new[] { "àlex", "ÀLEX", "l'éSSER" })
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/movies?q={Uri.EscapeDataString(q)}", ct);
            Assert.Equal(["Àlex i l'Ésser"], page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("title").GetString()));
        }
    }
}
