using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Bulk actions behind the library tables' multi-selection.</summary>
public sealed class BulkActionTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<int[]> AddMoviesAsync(int firstTmdbId, int count, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
        var movies = Enumerable.Range(firstTmdbId, count)
            .Select(i => new Movie { TmdbId = i, Title = $"Bulk {i}", SortTitle = $"bulk {i}", Year = 2000, Path = $"/movies/bulk-{i}", QualityProfileId = profile, Monitored = true })
            .ToList();
        db.Movies.AddRange(movies);
        await db.SaveChangesAsync(ct);
        return movies.Select(m => m.Id).ToArray();
    }

    private async Task<T> QueryAsync<T>(Func<KrakarrDbContext, Task<T>> query)
    {
        using var scope = factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>());
    }

    [Fact]
    public async Task Edits_monitoring_of_the_selected_movies_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_001, 3, ct);
        var client = await AdminAsync(ct);

        var response = await client.PutAsJsonAsync("/api/v1/movies/bulk", new { ids = ids[..2], monitored = false }, ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32());

        var monitored = await QueryAsync(db => db.Movies.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id).Select(m => m.Monitored).ToListAsync(ct));
        Assert.Equal([false, false, true], monitored);
    }

    [Fact]
    public async Task Rejects_empty_selections_and_unknown_profiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_101, 1, ct);
        var client = await AdminAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/movies/bulk", new { ids = Array.Empty<int>(), monitored = true }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/movies/bulk", new { ids }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/series/bulk", new { ids, qualityProfileId = 999_999 }, ct)).StatusCode);
    }

    [Fact]
    public async Task Deletes_the_selected_movies()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_201, 3, ct);
        var client = await AdminAsync(ct);

        var response = await client.PostAsJsonAsync("/api/v1/movies/bulk/delete", new { ids = ids[1..], deleteFiles = false }, ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32());

        var left = await QueryAsync(db => db.Movies.Where(m => ids.Contains(m.Id)).Select(m => m.Id).ToListAsync(ct));
        Assert.Equal([ids[0]], left);
    }

    [Fact]
    public async Task Deleting_titles_removes_their_pending_downloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_501, 2, ct);
        var series = await QueryAsync(async db =>
        {
            var profile = await db.QualityProfiles.Select(p => p.Id).FirstAsync(ct);
            var s = new Krakarr.Core.Domain.Series { TvdbId = 70_501, Title = "Bulk show", SortTitle = "bulk show", Path = "/tv/bulk-show", QualityProfileId = profile };
            db.Series.Add(s);
            await db.SaveChangesAsync(ct);
            return s.Id;
        });
        Download Row(string hash, bool imported, int? movieId = null, int? seriesId = null) => new()
        {
            InfoHash = hash, Name = hash, SavePath = "/downloads", MovieId = movieId, SeriesId = seriesId, Imported = imported,
            MediaType = movieId is null ? MediaType.Series : MediaType.Movie,
        };
        await QueryAsync(db =>
        {
            db.Downloads.AddRange(
                Row("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa7051", imported: false, movieId: ids[0]),
                Row("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb7051", imported: true, movieId: ids[0]),
                Row("cccccccccccccccccccccccccccccccccccc7051", imported: false, movieId: ids[1]),
                Row("dddddddddddddddddddddddddddddddddddd7051", imported: false, seriesId: series));
            return db.SaveChangesAsync(ct);
        });
        var client = await AdminAsync(ct);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/movies/{ids[0]}", ct)).StatusCode);
        (await client.PostAsJsonAsync("/api/v1/movies/bulk/delete", new { ids = ids[1..], deleteFiles = false }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/series/{series}", ct)).StatusCode);

        // Only the imported download survives, unlinked from the deleted movie.
        var left = await QueryAsync(db => db.Downloads.Where(d => d.InfoHash.EndsWith("7051")).Select(d => new { d.InfoHash, d.MovieId }).ToListAsync(ct));
        var kept = Assert.Single(left);
        Assert.StartsWith("bbbb", kept.InfoHash);
        Assert.Null(kept.MovieId);
    }

    [Fact]
    public async Task Queues_one_search_command_for_the_selection()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_301, 2, ct);
        var client = await AdminAsync(ct);

        var response = await client.PostAsJsonAsync("/api/v1/movies/bulk/search", new { ids }, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("Search: 2 movie(s)", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/movies/bulk/search", new { ids = new[] { 987_654 } }, ct)).StatusCode);
    }

    [Fact]
    public async Task Follows_and_unfollows_several_titles()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = await AddMoviesAsync(70_401, 3, ct);
        var client = await AdminAsync(ct);

        var follow = await client.PostAsJsonAsync("/api/v1/subscriptions/bulk", new { mediaType = "Movie", ids = ids.Append(987_654), subscribed = true }, ct);
        follow.EnsureSuccessStatusCode();
        Assert.Equal(3, (await follow.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32()); // unknown ids skipped

        // Following again adds nothing.
        var again = await client.PostAsJsonAsync("/api/v1/subscriptions/bulk", new { mediaType = "Movie", ids, subscribed = true }, ct);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32());

        var unfollow = await client.PostAsJsonAsync("/api/v1/subscriptions/bulk", new { mediaType = "Movie", ids = ids[..2], subscribed = false }, ct);
        Assert.Equal(2, (await unfollow.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32());

        var followed = (await client.GetFromJsonAsync<JsonElement[]>("/api/v1/subscriptions", ct))!
            .Select(s => s.GetProperty("libraryId").GetInt32()).Where(ids.Contains).ToList();
        Assert.Equal([ids[2]], followed);
    }
}
