using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads.Tests;

public sealed class ImportIssuesTests : IAsyncLifetime
{
    private const string PresentHash = "1111111111111111111111111111111111111111";
    private const string GoneHash = "2222222222222222222222222222222222222222";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeEngine _engine = new();
    private ServiceProvider _services = null!;
    private int _presentId, _goneId;

    private sealed class FakeEngine : ITorrentEngine
    {
        public List<string> Hashes { get; } = [];
        public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string infoHash) => Task.CompletedTask;
        public Task ResumeAsync(string infoHash) => Task.CompletedTask;
        public Task RemoveAsync(string infoHash, bool deleteData) => Task.CompletedTask;
        public IReadOnlyList<TorrentStatus> GetStatuses() => Hashes.Select(h => new TorrentStatus { InfoHash = h, Name = h, SavePath = "/d", Category = "tv" }).ToList();
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
        services.AddScoped<ImportIssues>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        var present = new Download
        {
            InfoHash = PresentHash, Name = "Present", SavePath = "/d", MediaType = MediaType.Series, SeriesId = 3, EpisodeIds = [1, 2],
            CompletedAt = DateTimeOffset.UtcNow, ImportMessage = "Import failed: no video files",
        };
        var gone = new Download { InfoHash = GoneHash, Name = "Gone", SavePath = "/d", MovieId = 5, ImportMessage = "Removed from qBittorrent" };
        db.Downloads.AddRange(present, gone,
            new Download { InfoHash = "3333333333333333333333333333333333333333", Name = "Fine", SavePath = "/d", MovieId = 6 },
            new Download { InfoHash = "4444444444444444444444444444444444444444", Name = "Done", SavePath = "/d", MovieId = 7, Imported = true, ImportMessage = "old" });
        await db.SaveChangesAsync();
        (_presentId, _goneId) = (present.Id, gone.Id);
        _engine.Hashes.Add(PresentHash);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<T> Run<T>(Func<ImportIssues, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ImportIssues>());
    }

    private async Task<Download?> Row(int id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Downloads.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    [Fact]
    public async Task Lists_failed_imports_with_whether_the_torrent_is_still_in_the_client()
    {
        var issues = await Run(i => i.ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["Gone", "Present"], issues.Select(i => i.Name).Order());
        Assert.True(issues.Single(i => i.Name == "Present").InClient);
        Assert.False(issues.Single(i => i.Name == "Gone").InClient);
        Assert.Equal(5, issues.Single(i => i.Name == "Gone").MovieId);
    }

    [Fact]
    public async Task Retry_clears_the_message_so_the_import_runs_again()
    {
        Assert.True(await Run(i => i.RetryAsync(_presentId, TestContext.Current.CancellationToken)));

        var row = await Row(_presentId);
        Assert.Null(row!.ImportMessage);
        Assert.Equal(3, row.SeriesId);
    }

    [Fact]
    public async Task Retry_refuses_a_download_that_left_the_client()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(i => i.RetryAsync(_goneId, TestContext.Current.CancellationToken)));
        Assert.NotNull((await Row(_goneId))!.ImportMessage);
    }

    [Fact]
    public async Task Dismiss_forgets_a_download_that_left_the_client()
    {
        Assert.True(await Run(i => i.DismissAsync(_goneId, TestContext.Current.CancellationToken)));
        Assert.Null(await Row(_goneId));
    }

    [Fact]
    public async Task Dismiss_keeps_a_torrent_still_in_the_client_but_unlinks_it()
    {
        Assert.True(await Run(i => i.DismissAsync(_presentId, TestContext.Current.CancellationToken)));

        var row = await Row(_presentId);
        Assert.NotNull(row);
        Assert.Null(row.ImportMessage);
        Assert.Null(row.SeriesId);
        Assert.Null(row.MediaType);
        Assert.Empty(row.EpisodeIds);
        Assert.DoesNotContain(await Run(i => i.ListAsync(TestContext.Current.CancellationToken)), x => x.Id == _presentId);
    }

    [Fact]
    public async Task Unknown_or_healthy_downloads_are_not_issues()
    {
        Assert.False(await Run(i => i.DismissAsync(999, TestContext.Current.CancellationToken)));
        Assert.False(await Run(i => i.RetryAsync(999, TestContext.Current.CancellationToken)));
    }
}
