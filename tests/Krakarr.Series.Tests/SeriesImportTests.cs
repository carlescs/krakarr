using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Series.Tests;

/// <summary>Runs the real import pipeline against an in-memory database and real files on disk.</summary>
public sealed class SeriesImportTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-series");
    private readonly FakeEngine _engine = new();
    private ServiceProvider _services = null!;

    private sealed class FakeEngine : ITorrentEngine
    {
        public List<(string Path, long Length)> Files { get; } = [];
        public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string infoHash) => Task.CompletedTask;
        public Task ResumeAsync(string infoHash) => Task.CompletedTask;
        public Task RemoveAsync(string infoHash, bool deleteData) => Task.CompletedTask;
        public IReadOnlyList<TorrentStatus> GetStatuses() => [];
        public Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string Path, long Length)>>(Files);
        public void MarkImported(string infoHash) { }
        public Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default) => Task.FromResult<TorrentDetails?>(null);
    }

    private sealed class Unused : ITvdbClient, IIndexerSearchService, IDownloadService
    {
        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> GrabAsync(ReleaseInfo release, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddKrakarrMedia();
        var unused = new Unused();
        services.AddSingleton<ITvdbClient>(unused);
        services.AddSingleton<IIndexerSearchService>(unused);
        services.AddSingleton<IDownloadService>(unused);
        services.AddSingleton<ITorrentEngine>(_engine);
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SeriesService>();
        services.AddScoped<SeriesImportService>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        var profile = new QualityProfile { Name = "HD", AllowedQualityIds = [Qualities.WebDl1080.Id, Qualities.Bluray1080.Id], CutoffQualityId = Qualities.Bluray1080.Id };
        db.QualityProfiles.Add(profile);
        var series = new Core.Domain.Series { TvdbId = 1, Title = "Test Show", Year = 2021, Path = Path.Combine(_root.FullName, "tv", "Test Show (2021)"), QualityProfile = profile };
        for (var e = 1; e <= 3; e++)
            series.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = e, Title = $"Chapter {e}", AirDate = new DateOnly(2021, 1, e) });
        series.Seasons.Add(new Season { SeasonNumber = 1 });
        db.Series.Add(series);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _root.Delete(true);
    }

    private string WriteFile(string relative, int bytes = 2048)
    {
        var path = Path.Combine(_root.FullName, "downloads", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        _engine.Files.Add((path, bytes));
        return path;
    }

    private async Task<Download> AddDownloadAsync(string name, int qualityId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var download = new Download
        {
            InfoHash = Guid.NewGuid().ToString("N"), Name = name, SavePath = Path.Combine(_root.FullName, "downloads"), SeriesId = 1,
            EpisodeIds = [1, 2, 3], QualityId = qualityId, CompletedAt = DateTimeOffset.UtcNow,
        };
        db.Downloads.Add(download);
        await db.SaveChangesAsync();
        return download;
    }

    private async Task RunImportAsync()
    {
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SeriesImportService>().ExecuteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Imports_a_season_pack_into_season_folder_with_hardlinks()
    {
        using (var scope = _services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>()
                .SaveAsync(new MediaManagementSettings { MinimumVideoFileSizeMb = 0 }, TestContext.Current.CancellationToken);
        }

        const string pack = "Test.Show.S01.1080p.WEB-DL.x264-KRK";
        WriteFile($"{pack}/Test.Show.S01E01.1080p.WEB-DL.x264-KRK.mkv");
        WriteFile($"{pack}/Test.Show.S01E02.1080p.WEB-DL.x264-KRK.mkv");
        WriteFile($"{pack}/Test.Show.S01E03.1080p.WEB-DL.x264-KRK.mkv");
        WriteFile($"{pack}/Sample/test.show.s01e01.sample.mkv", 100);
        WriteFile($"{pack}/Test.Show.S01.nfo", 10);
        await AddDownloadAsync(pack, Qualities.WebDl1080.Id);

        await RunImportAsync();

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var episodes = await db.Episodes.Include(e => e.EpisodeFile).OrderBy(e => e.EpisodeNumber).ToListAsync();
        Assert.All(episodes, e => Assert.NotNull(e.EpisodeFile));
        Assert.Equal(Path.Combine("Season 01", "Test Show - S01E02 - Chapter 2 [WEBDL-1080p].mkv"), episodes[1].EpisodeFile!.RelativePath);
        var series = await db.Series.SingleAsync();
        Assert.True(File.Exists(Path.Combine(series.Path, episodes[0].EpisodeFile!.RelativePath)));
        Assert.Equal(3, await db.EpisodeFiles.CountAsync());
        Assert.Equal("Test.Show.S01E02.1080p.WEB-DL.x264-KRK", episodes[1].EpisodeFile!.SceneName); // per-file name, not the pack name
        var download = await db.Downloads.SingleAsync();
        Assert.True(download.Imported);
        var history = await db.History.SingleAsync();
        Assert.Equal(HistoryEventType.Imported, history.EventType);
        Assert.StartsWith("Imported S01E01E02E03", history.Message);
    }

    [Fact]
    public async Task Upgrade_replaces_old_file_and_worse_quality_is_skipped()
    {
        using (var scope = _services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>()
                .SaveAsync(new MediaManagementSettings { MinimumVideoFileSizeMb = 0 }, TestContext.Current.CancellationToken);
        }

        WriteFile("Test.Show.S01E01.1080p.WEB-DL.x264-KRK.mkv");
        await AddDownloadAsync("Test.Show.S01E01.1080p.WEB-DL.x264-KRK", Qualities.WebDl1080.Id);
        await RunImportAsync();

        _engine.Files.Clear();
        WriteFile("Test.Show.S01E01.1080p.BluRay.x264-KRK.mkv");
        await AddDownloadAsync("Test.Show.S01E01.1080p.BluRay.x264-KRK", Qualities.Bluray1080.Id);
        await RunImportAsync();

        using (var check = _services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var file = await db.EpisodeFiles.SingleAsync();
            Assert.Equal(Qualities.Bluray1080.Id, file.QualityId);
            var seasonDir = Path.Combine((await db.Series.SingleAsync()).Path, "Season 01");
            Assert.Equal(["Test Show - S01E01 - Chapter 1 [Bluray-1080p].mkv"], Directory.GetFiles(seasonDir).Select(Path.GetFileName));
        }

        _engine.Files.Clear();
        WriteFile("Test.Show.S01E01.1080p.WEB-DL.x264-OLD.mkv");
        await AddDownloadAsync("Test.Show.S01E01.1080p.WEB-DL.x264-OLD", Qualities.WebDl1080.Id);
        await RunImportAsync();

        using var final = _services.CreateScope();
        var finalDb = final.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var last = await finalDb.Downloads.OrderBy(d => d.Id).LastAsync();
        Assert.False(last.Imported);
        Assert.Contains("existing file is better", last.ImportMessage);
    }

    [Fact]
    public async Task Fails_when_files_cannot_be_mapped()
    {
        WriteFile("Random.Video.Name.mkv");
        WriteFile("Another.Video.mkv");
        await AddDownloadAsync("Test.Show.S01.1080p.WEB-DL.x264-KRK", Qualities.WebDl1080.Id);
        await RunImportAsync();

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var download = await db.Downloads.SingleAsync();
        Assert.False(download.Imported);
        Assert.Contains("could not determine the episode", download.ImportMessage);
        Assert.Equal(0, await db.EpisodeFiles.CountAsync());
    }
}
