using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads.Tests;

public sealed class DownloadMediaTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeEngine _engine = new();
    private ServiceProvider _services = null!;
    private int _movieDownload, _seriesDownload, _issueDownload, _manualDownload, _notInClient;

    private sealed class FakeEngine : ITorrentEngine
    {
        public List<TorrentStatus> Statuses { get; } = [];
        public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string infoHash) => Task.CompletedTask;
        public Task ResumeAsync(string infoHash) => Task.CompletedTask;
        public Task RemoveAsync(string infoHash, bool deleteData) => Task.CompletedTask;
        public IReadOnlyList<TorrentStatus> GetStatuses() => Statuses;
        public Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default) => throw new NotSupportedException();
        public void MarkImported(string infoHash) { }
        public Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default) => Task.FromResult<TorrentDetails?>(null);
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddSingleton<ITorrentEngine>(_engine);
        services.AddScoped<DownloadMedia>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        var profile = new QualityProfile { Name = "HD", AllowedQualityIds = [17], CutoffQualityId = 17 };
        db.QualityProfiles.Add(profile);
        await db.SaveChangesAsync();
        var movie = new Movie { TmdbId = 1, Title = "Arrival", Year = 2016, Path = "/movies/Arrival", QualityProfileId = profile.Id };
        var series = new Series { TmdbId = 2, Title = "Severance", Year = 2022, Path = "/tv/Severance", QualityProfileId = profile.Id };
        db.AddRange(movie, series);
        await db.SaveChangesAsync();
        var episodes = new[] { 1, 2, 3, 5 }.Select(n => new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = n }).ToList();
        db.Episodes.AddRange(episodes);
        await db.SaveChangesAsync();

        var movieDl = Dl("1", m => m.MovieId = movie.Id);
        var seriesDl = Dl("2", d => { d.SeriesId = series.Id; d.EpisodeIds = episodes.Select(e => e.Id).ToList(); });
        var issueDl = Dl("3", d => { d.MovieId = movie.Id; d.ImportMessage = "No video files"; });
        var manualDl = Dl("4", _ => { });
        var oldDl = Dl("5", d => { d.MovieId = movie.Id; d.Imported = true; });
        db.Downloads.AddRange(movieDl, seriesDl, issueDl, manualDl, oldDl);
        await db.SaveChangesAsync();
        (_movieDownload, _seriesDownload, _issueDownload, _manualDownload, _notInClient) = (movieDl.Id, seriesDl.Id, issueDl.Id, manualDl.Id, oldDl.Id);
        foreach (var d in new[] { movieDl, seriesDl, manualDl })
        {
            _engine.Statuses.Add(new TorrentStatus
            {
                Id = d.Id, InfoHash = d.InfoHash, Name = d.Name, SavePath = "/d", Category = "x", MovieId = d.MovieId, SeriesId = d.SeriesId,
            });
        }
    }

    private static Download Dl(string hashDigit, Action<Download> setup)
    {
        var d = new Download { InfoHash = new string(hashDigit[0], 40), Name = "Torrent " + hashDigit, SavePath = "/d" };
        setup(d);
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Names_the_movie_or_series_of_downloads_in_the_client_and_of_failed_imports()
    {
        using var scope = _services.CreateScope();
        var list = await scope.ServiceProvider.GetRequiredService<DownloadMedia>().ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_issueDownload, _seriesDownload, _movieDownload], list.Select(i => i.Id));
        var movie = list.Single(i => i.Id == _movieDownload);
        Assert.Equal(("Arrival", 2016, (string?)null), (movie.Title, movie.Year, movie.Episodes));
        Assert.NotNull(movie.MovieId);
        var series = list.Single(i => i.Id == _seriesDownload);
        Assert.Equal(("Severance", "S01E01–E03 E05"), (series.Title, series.Episodes));
        Assert.NotNull(series.SeriesId);
        Assert.DoesNotContain(list, i => i.Id == _manualDownload || i.Id == _notInClient);
    }

    [Fact]
    public void Episode_codes_collapse_ranges_and_separate_seasons()
    {
        Assert.Null(DownloadMedia.EpisodeCodes([]));
        Assert.Equal("S01E04", DownloadMedia.EpisodeCodes([(1, 4)]));
        Assert.Equal("S01E01–E03, S02E01 E03", DownloadMedia.EpisodeCodes([(2, 3), (1, 2), (1, 1), (2, 1), (1, 3), (1, 2)]));
        Assert.Equal("S01E01 E03 E05 E07 E09 E11 +2 more", DownloadMedia.EpisodeCodes(Enumerable.Range(0, 8).Select(i => (1, 1 + 2 * i))));
    }
}
