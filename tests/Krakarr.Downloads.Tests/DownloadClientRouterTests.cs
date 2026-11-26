using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads.Tests;

public sealed class DownloadClientRouterTests : IAsyncLifetime
{
    private const string EmbeddedHash = "1111111111111111111111111111111111111111";
    private const string QbtHash = "2222222222222222222222222222222222222222";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeClient _embedded = new(DownloadClientKind.Embedded, DownloadClients.Embedded);
    private readonly FakeClient _qbt = new(DownloadClientKind.QBittorrent, DownloadClients.QBittorrent);
    private ServiceProvider _services = null!;
    private DownloadClientRouter _router = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Downloads.AddRange(
            new Download { Client = DownloadClients.Embedded, InfoHash = EmbeddedHash, Name = "Old", SavePath = "/d" },
            new Download { Client = DownloadClients.QBittorrent, InfoHash = QbtHash, Name = "New", SavePath = "/d" });
        await db.SaveChangesAsync();
        // qBittorrent is the selected client; the built-in one still owns a torrent.
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new DownloadClientSettings { Client = DownloadClientKind.QBittorrent });

        _router = new DownloadClientRouter([_embedded, _qbt], _services.GetRequiredService<SettingsCache>(), _services.GetRequiredService<IServiceScopeFactory>());
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Controls_go_to_the_client_that_owns_the_torrent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _router.PauseAsync(EmbeddedHash);
        await _router.ResumeAsync(EmbeddedHash.ToUpperInvariant());
        await _router.RemoveAsync(QbtHash, deleteData: true);
        await _router.GetFilesAsync(EmbeddedHash, ct);
        await _router.GetDetailsAsync(QbtHash, ct);

        Assert.Equal(["pause " + EmbeddedHash, "resume " + EmbeddedHash.ToUpperInvariant(), "files " + EmbeddedHash], _embedded.Calls);
        Assert.Equal(["remove " + QbtHash, "details " + QbtHash], _qbt.Calls);
    }

    [Fact]
    public async Task New_torrents_and_unknown_hashes_go_to_the_selected_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await _router.AddMagnetAsync("magnet:?xt=urn:btih:3333333333333333333333333333333333333333", new AddTorrentOptions(), ct);
        await _router.PauseAsync("4444444444444444444444444444444444444444");

        Assert.Empty(_embedded.Calls);
        Assert.Equal(["add", "pause 4444444444444444444444444444444444444444"], _qbt.Calls);
    }

    [Fact]
    public async Task Adding_a_torrent_again_forgets_a_row_whose_torrent_left_every_client()
    {
        var ct = TestContext.Current.CancellationToken;
        await AgeRowsAsync(ct);
        _qbt.StaleHash = EmbeddedHash; // the row is the built-in client's, but that engine no longer runs the torrent

        var added = await _router.AddMagnetAsync("magnet:?xt=urn:btih:" + EmbeddedHash, new AddTorrentOptions(), ct);

        Assert.Equal("x", added.Name);
        Assert.Equal(["add", "add"], _qbt.Calls);
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        Assert.False(await db.Downloads.AnyAsync(d => d.InfoHash == EmbeddedHash, ct));
    }

    [Fact]
    public async Task Adding_a_torrent_a_client_still_runs_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await AgeRowsAsync(ct);
        _qbt.StaleHash = EmbeddedHash;
        _embedded.Statuses = [Status(EmbeddedHash, DownloadClients.Embedded, daysAgo: 3)];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _router.AddMagnetAsync("magnet:?xt=urn:btih:" + EmbeddedHash, new AddTorrentOptions(), ct));

        Assert.Equal("Torrent already exists: Old", ex.Message);
        Assert.Equal(["add"], _qbt.Calls);
    }

    [Fact]
    public async Task A_freshly_added_row_is_not_treated_as_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        _qbt.StaleHash = QbtHash; // just added, qBittorrent doesn't list it yet

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _router.AddMagnetAsync("magnet:?xt=urn:btih:" + QbtHash, new AddTorrentOptions(), ct));

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        Assert.True(await db.Downloads.AnyAsync(d => d.InfoHash == QbtHash, ct));
    }

    private async Task AgeRowsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        foreach (var row in await db.Downloads.ToListAsync(ct)) row.Added = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public void Statuses_are_merged_from_every_client_newest_first()
    {
        _embedded.Statuses = [Status(EmbeddedHash, DownloadClients.Embedded, daysAgo: 3)];
        _qbt.Statuses = [Status(QbtHash, DownloadClients.QBittorrent, daysAgo: 1)];

        Assert.Equal([QbtHash, EmbeddedHash], _router.GetStatuses().Select(s => s.InfoHash));
    }

    [Fact]
    public async Task Feed_publishes_the_latest_torrents_of_every_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new RecordingHub();
        var feed = new DownloadStatusFeed(hub);

        await feed.PublishAsync(DownloadClientKind.Embedded, [Status(EmbeddedHash, DownloadClients.Embedded, daysAgo: 3)], ct);
        await feed.PublishAsync(DownloadClientKind.QBittorrent, [Status(QbtHash, DownloadClients.QBittorrent, daysAgo: 1)], ct);
        Assert.Equal([QbtHash, EmbeddedHash], hub.Last.Select(s => s.InfoHash));

        await feed.PublishAsync(DownloadClientKind.QBittorrent, [], ct);
        Assert.Equal([EmbeddedHash], hub.Last.Select(s => s.InfoHash));

        // A client that had nothing and still has nothing doesn't publish.
        var count = hub.Count;
        await feed.PublishAsync(DownloadClientKind.QBittorrent, [], ct);
        Assert.Equal(count, hub.Count);
    }

    private static TorrentStatus Status(string hash, string client, int daysAgo) => new()
    {
        InfoHash = hash, Name = hash, Category = "default", SavePath = "/d", Client = client,
        Added = DateTimeOffset.UtcNow.AddDays(-daysAgo),
    };

    private sealed class RecordingHub : IEventHub
    {
        public int Count { get; private set; }
        public IReadOnlyList<TorrentStatus> Last { get; private set; } = [];

        public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
        {
            Count++;
            Last = (IReadOnlyList<TorrentStatus>)payload!;
            return Task.CompletedTask;
        }

        public Task PublishToUserAsync(int userId, string eventName, object? payload, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task Health_check_reports_clients_that_are_not_working()
    {
        var check = new DownloadClientHealthCheck(_router);
        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));

        // The selected client failing stops downloads: an error.
        _qbt.LastError = "Connection refused";
        var error = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Krakarr.Core.Health.HealthSeverity.Error, error.Severity);
        Assert.Contains("qBittorrent is not working", error.Message);
        Assert.Contains("Connection refused", error.Message);

        // A previous client that still owns downloads: a warning; once it owns none, it doesn't matter.
        _qbt.LastError = null;
        _embedded.LastError = "Port in use";
        Assert.Equal(Krakarr.Core.Health.HealthSeverity.Warning, Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken)).Severity);
        _embedded.IsActive = false;
        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeClient(DownloadClientKind kind, string name) : IDownloadClientImpl
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<TorrentStatus> Statuses { get; set; } = [];
        /// <summary>When set, the first add reports an existing row for this hash, like the real clients do.</summary>
        public string? StaleHash { get; set; }
        public DownloadClientKind Kind => kind;
        public string Name => name;
        public bool IsActive { get; set; } = true;
        public string? LastError { get; set; }

        public Task<DownloadClientTestResult> TestAsync(DownloadClientSettings settings, CancellationToken ct = default) =>
            Task.FromResult(new DownloadClientTestResult(true, null, null));

        public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) => Add();
        public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) => Add();

        private Task<Download> Add()
        {
            Calls.Add("add");
            if (StaleHash is { } stale && Calls.Count(c => c == "add") == 1) throw new StaleDownloadException(stale);
            return Task.FromResult(new Download { Client = name, InfoHash = "x", Name = "x", SavePath = "/d" });
        }

        public Task PauseAsync(string infoHash) => Record("pause " + infoHash);
        public Task ResumeAsync(string infoHash) => Record("resume " + infoHash);
        public Task RemoveAsync(string infoHash, bool deleteData) => Record("remove " + infoHash);
        public IReadOnlyList<TorrentStatus> GetStatuses() => Statuses;

        public async Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default)
        {
            await Record("files " + infoHash);
            return [];
        }

        public void MarkImported(string infoHash) { }

        public async Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default)
        {
            await Record("details " + infoHash);
            return null;
        }

        private Task Record(string call)
        {
            Calls.Add(call);
            return Task.CompletedTask;
        }
    }
}
