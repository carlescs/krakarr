using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads.QBittorrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Downloads.Tests;

public sealed class QBittorrentEngineTests : IAsyncLifetime
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeQBittorrent _fake = new();
    private ServiceProvider _services = null!;
    private QBittorrentEngine _engine = null!;
    private DownloadClientSettings _settings = null!;

    private sealed class FakeFactory(FakeQBittorrent fake) : QBittorrentApiFactory
    {
        public override QBittorrentApi Create(QBittorrentSettings settings) => new(settings, fake);
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Database.EnsureCreatedAsync();
            _settings = new DownloadClientSettings
            {
                Client = DownloadClientKind.QBittorrent,
                QBittorrent = new QBittorrentSettings
                {
                    Url = "http://qbt.local:8080", Username = "admin", Password = "secret", Tag = "krakarr",
                    RemotePathPrefix = "/downloads", LocalPathPrefix = "/data/downloads",
                },
            };
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(_settings);
        }

        _engine = new QBittorrentEngine(_services.GetRequiredService<IServiceScopeFactory>(), new FakeFactory(_fake), new NullEventHub(), NullLogger<QBittorrentEngine>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Dispose();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<T> WithDb<T>(Func<KrakarrDbContext, Task<T>> f)
    {
        using var scope = _services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>());
    }

    [Fact]
    public async Task Adds_with_tag_and_tracks_progress_until_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = await _engine.AddMagnetAsync($"magnet:?xt=urn:btih:{Hash}&dn=Movie.2020.1080p", new AddTorrentOptions { Category = "movies", MovieId = 5 }, ct);

        Assert.Equal(DownloadClients.QBittorrent, row.Client);
        Assert.Equal(Hash, row.InfoHash);
        var add = _fake.Requests.Single(r => r.Path == "/api/v2/torrents/add");
        Assert.Contains("krakarr", add.Body);
        Assert.Contains(_fake.Requests, r => r.Path == "/api/v2/torrents/createTags" && r.Body.Contains("krakarr"));

        _fake.Torrents.Add(new(Hash, "Movie.2020.1080p", "krakarr", Progress: 0.5));
        await _engine.SyncAsync(_settings, ct);
        var status = Assert.Single(_engine.GetStatuses());
        Assert.Equal(50, status.Progress);
        Assert.Equal(DownloadState.Downloading, status.State);
        Assert.Equal(5, status.MovieId);

        _fake.Torrents[0] = _fake.Torrents[0] with { Progress = 1, State = "uploading" };
        await _engine.SyncAsync(_settings, ct);
        var completed = await WithDb(db => db.Downloads.SingleAsync(d => d.InfoHash == Hash, ct));
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(DownloadState.Seeding, completed.State);
        Assert.Equal("/data/downloads/", completed.SavePath); // remote path mapped to local

        var files = await _engine.GetFilesAsync(Hash, ct);
        Assert.Contains(files, f => f.Path == "/data/downloads/Movie.2020.1080p/Movie.2020.1080p.mkv");
    }

    [Fact]
    public async Task Never_shows_or_controls_untagged_torrents()
    {
        var ct = TestContext.Current.CancellationToken;
        _fake.Torrents.Add(new("ffffffffffffffffffffffffffffffffffffffff", "Someone else's", "linux-isos"));
        await _engine.SyncAsync(_settings, ct);

        Assert.Empty(_engine.GetStatuses());
        Assert.Equal(0, await WithDb(db => db.Downloads.CountAsync(ct)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _engine.PauseAsync("ffffffffffffffffffffffffffffffffffffffff"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _engine.RemoveAsync("ffffffffffffffffffffffffffffffffffffffff", true));
        Assert.DoesNotContain(_fake.Requests, r => r.Path is "/api/v2/torrents/pause" or "/api/v2/torrents/delete");
    }

    [Fact]
    public async Task Picks_up_torrents_tagged_manually_in_qbittorrent()
    {
        var ct = TestContext.Current.CancellationToken;
        _fake.Torrents.Add(new(Hash, "Manual", "krakarr"));
        await _engine.SyncAsync(_settings, ct);

        var row = await WithDb(db => db.Downloads.SingleAsync(ct));
        Assert.Equal("Manual", row.Name);
        Assert.Null(row.MovieId);
        Assert.Single(_engine.GetStatuses());
    }

    [Fact]
    public async Task Torrent_removed_in_qbittorrent_frees_the_movie()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDb(async db =>
        {
            db.Downloads.Add(new Download
            {
                Client = DownloadClients.QBittorrent, InfoHash = Hash, Name = "Gone", SavePath = "/x", MovieId = 3,
                Added = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            db.Downloads.Add(new Download
            {
                Client = DownloadClients.QBittorrent, InfoHash = "1111111111111111111111111111111111111111", Name = "Manual gone", SavePath = "/x",
                Added = DateTimeOffset.UtcNow.AddMinutes(-10),
            });
            return await db.SaveChangesAsync(ct);
        });

        await _engine.SyncAsync(_settings, ct);

        var rows = await WithDb(db => db.Downloads.ToListAsync(ct));
        var movieRow = Assert.Single(rows);
        Assert.Equal(QBittorrentEngine.RemovedMessage, movieRow.ImportMessage);
    }

    [Fact]
    public async Task Ignores_rows_owned_by_the_builtin_engine()
    {
        var ct = TestContext.Current.CancellationToken;
        await WithDb(async db =>
        {
            db.Downloads.Add(new Download { Client = DownloadClients.Embedded, InfoHash = Hash, Name = "Embedded", SavePath = "/x", Added = DateTimeOffset.UtcNow.AddHours(-1) });
            return await db.SaveChangesAsync(ct);
        });

        await _engine.SyncAsync(_settings, ct);
        var row = await WithDb(db => db.Downloads.SingleAsync(ct));
        Assert.Null(row.ImportMessage);
    }

    [Fact]
    public async Task Test_reports_version_and_tag_count()
    {
        _fake.Torrents.Add(new(Hash, "Ours", "krakarr"));
        var result = await _engine.TestAsync(_settings, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Contains("v4.6.3", result.Version);
        Assert.Contains("1 torrent", result.Message);

        var bad = await _engine.TestAsync(_settings with { QBittorrent = _settings.QBittorrent with { Password = "wrong" } }, TestContext.Current.CancellationToken);
        Assert.False(bad.Success);
    }

    [Fact]
    public async Task Matches_hybrid_torrents_whose_qbittorrent_id_is_the_truncated_v2_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        const string v2 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbbbbbbbb";
        var qbtId = v2[..40];
        await _engine.AddMagnetAsync($"magnet:?xt=urn:btih:{Hash}&dn=Hybrid", new AddTorrentOptions { MovieId = 9 }, ct);
        _fake.Torrents.Add(new(qbtId, "Hybrid", "krakarr", Progress: 1, State: "uploading", V1: Hash, V2: v2));

        await _engine.SyncAsync(_settings, ct);

        var row = await WithDb(db => db.Downloads.SingleAsync(ct)); // no duplicate "manual" row
        Assert.Equal(Hash, row.InfoHash);
        Assert.Equal(9, row.MovieId);
        Assert.NotNull(row.CompletedAt);
        Assert.Single(_engine.GetStatuses(), s => s.InfoHash == Hash);

        // Control calls use qBittorrent's own ID.
        await _engine.PauseAsync(Hash);
        Assert.Contains(_fake.Requests, r => r.Path == "/api/v2/torrents/pause" && r.Body.Contains(qbtId));
        var files = await _engine.GetFilesAsync(Hash, ct);
        Assert.NotEmpty(files);
        Assert.Contains(_fake.Requests, r => r.Path == "/api/v2/torrents/files" && r.Query.Contains(qbtId));
        await _engine.RemoveAsync(Hash, deleteData: false);
        Assert.Empty(_fake.Torrents);
    }
}
