using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Host.Tests;

/// <summary>Deleting single movie and episode files from the library.</summary>
public sealed class FileDeletionTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>, IDisposable
{
    private readonly string _media = Directory.CreateTempSubdirectory("krakarr-media").FullName;

    public void Dispose() => Directory.Delete(_media, recursive: true);

    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private async Task<T> WithDbAsync<T>(Func<KrakarrDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>());
    }

    private string Touch(params string[] segments)
    {
        var path = Path.Combine([_media, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "video");
        return path;
    }

    [Fact]
    public async Task Deletes_a_movie_file_and_optionally_unmonitors_the_movie()
    {
        var ct = TestContext.Current.CancellationToken;
        var video = Touch("Film (2001)", "Film (2001).mkv");
        var subtitle = Touch("Film (2001)", "Film (2001).en.srt");
        var (movieId, fileId) = await WithDbAsync(async db =>
        {
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var movie = new Movie { TmdbId = 81_001, Title = "Film", SortTitle = "film", Year = 2001, Path = Path.Combine(_media, "Film (2001)"), QualityProfileId = profile };
            db.Movies.Add(movie);
            await db.SaveChangesAsync(ct);
            var file = new MovieFile { MovieId = movie.Id, RelativePath = "Film (2001).mkv", Size = 5, QualityId = Qualities.Bluray1080.Id };
            db.MovieFiles.Add(file);
            await db.SaveChangesAsync(ct);
            movie.MovieFileId = file.Id;
            db.SubtitleFiles.Add(new SubtitleFile { MovieFileId = file.Id, Language = "en", Path = subtitle, Provider = "disk" });
            await db.SaveChangesAsync(ct);
            return (movie.Id, file.Id);
        });
        var client = await AdminAsync(ct);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/moviefiles/{fileId}?unmonitor=true", ct)).StatusCode);

        Assert.False(File.Exists(video));
        Assert.False(File.Exists(subtitle));
        var state = await WithDbAsync(async db => new
        {
            Movie = await db.Movies.AsNoTracking().FirstAsync(m => m.Id == movieId, ct),
            Files = await db.MovieFiles.CountAsync(f => f.Id == fileId, ct),
            Subtitles = await db.SubtitleFiles.CountAsync(s => s.MovieFileId == fileId, ct),
            Deleted = await db.History.CountAsync(h => h.MovieId == movieId && h.EventType == HistoryEventType.FileDeleted, ct),
        });
        Assert.Null(state.Movie.MovieFileId);
        Assert.False(state.Movie.Monitored);
        Assert.Equal(0, state.Files);
        Assert.Equal(0, state.Subtitles);
        Assert.Equal(1, state.Deleted);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/moviefiles/{fileId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Deletes_episode_files_in_bulk_and_keeps_the_episodes()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Touch("Show", "Season 01", "S01E01.mkv");
        var second = Touch("Show", "Season 01", "S01E02.mkv");
        var kept = Touch("Show", "Season 01", "S01E03.mkv");
        var (seriesId, fileIds) = await WithDbAsync(async db =>
        {
            var profile = await db.QualityProfiles.MinAsync(p => p.Id, ct);
            var series = new Krakarr.Core.Domain.Series { TvdbId = 82_001, Title = "Show", SortTitle = "show", Year = 2010, Path = Path.Combine(_media, "Show"), QualityProfileId = profile };
            db.Series.Add(series);
            await db.SaveChangesAsync(ct);
            var files = new[] { "S01E01.mkv", "S01E02.mkv", "S01E03.mkv" }
                .Select(name => new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = Path.Combine("Season 01", name), Size = 5, QualityId = Qualities.Hdtv720.Id })
                .ToList();
            db.EpisodeFiles.AddRange(files);
            await db.SaveChangesAsync(ct);
            db.Episodes.AddRange(files.Select((f, i) => new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = i + 1, EpisodeFileId = f.Id }));
            await db.SaveChangesAsync(ct);
            return (series.Id, files.Select(f => f.Id).ToArray());
        });
        var client = await AdminAsync(ct);

        var response = await client.PostAsJsonAsync("/api/v1/episodefiles/bulk/delete", new { ids = fileIds[..2], unmonitor = true }, ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("affected").GetInt32());

        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(kept));
        var episodes = await WithDbAsync(db => db.Episodes.AsNoTracking().Where(e => e.SeriesId == seriesId).OrderBy(e => e.EpisodeNumber)
            .Select(e => new { e.EpisodeFileId, e.Monitored }).ToListAsync(ct));
        Assert.Equal(3, episodes.Count);
        Assert.Equal([null, null, fileIds[2]], episodes.Select(e => e.EpisodeFileId));
        Assert.Equal([false, false, true], episodes.Select(e => e.Monitored));

        // Single delete removes the last file and, with it, the emptied season folder.
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/episodefiles/{fileIds[2]}", ct)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_media, "Show", "Season 01")));
        Assert.True(await WithDbAsync(db => db.Episodes.AnyAsync(e => e.SeriesId == seriesId && e.EpisodeNumber == 3 && e.Monitored && e.EpisodeFileId == null, ct)));
    }
}
