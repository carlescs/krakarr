using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Indexers.Tests;

public sealed class IndexerSearchServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    /// <summary>"Good" returns one release, "Broken" throws, anything else times out (cancelled).</summary>
    private sealed class FakeTorznab : ITorznabClient
    {
        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default) => indexer.Name switch
        {
            "Good" => Task.FromResult<IReadOnlyList<ReleaseInfo>>(
                [new ReleaseInfo { Title = "Movie.2024.1080p", Guid = "g1", IndexerId = indexer.Id, IndexerName = indexer.Name }]),
            "Broken" => throw new IndexerException("401 Unauthorized"),
            _ => throw new TaskCanceledException(),
        };

        public Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class Collect : IProgress<IReadOnlyList<IndexerQueryStatus>>
    {
        public List<IReadOnlyList<IndexerQueryStatus>> Reports { get; } = [];
        public void Report(IReadOnlyList<IndexerQueryStatus> value)
        {
            lock (Reports) Reports.Add(value);
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Indexers.AddRange(
            new Indexer { Name = "Good", BaseUrl = "http://good", Enabled = true },
            new Indexer { Name = "Broken", BaseUrl = "http://broken", Enabled = true },
            new Indexer { Name = "Slow", BaseUrl = "http://slow", Enabled = true },
            new Indexer { Name = "Disabled", BaseUrl = "http://off", Enabled = false });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Reports_each_indexer_status_as_it_finishes()
    {
        using var scope = _services.CreateScope();
        var service = new IndexerSearchService(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>(), new FakeTorznab(), NullLogger<IndexerSearchService>.Instance);
        var progress = new Collect();

        var result = await service.SearchAsync(new SearchCriteria { Type = SearchType.Movie, Query = "movie" }, progress: progress,
            ct: TestContext.Current.CancellationToken);

        // First all pending, then one report per finished indexer; the disabled one is never queried.
        Assert.Equal(4, progress.Reports.Count);
        Assert.All(progress.Reports[0], s => Assert.Equal(IndexerQueryState.Pending, s.State));
        Assert.Equal(["Broken", "Good", "Slow"], progress.Reports[0].Select(s => s.IndexerName).Order());
        Assert.All(progress.Reports.Skip(1), r => Assert.Equal(3, r.Count));

        var byName = result.Indexers.ToDictionary(s => s.IndexerName);
        Assert.Equal(3, byName.Count);
        Assert.Equal(IndexerQueryState.Done, byName["Good"].State);
        Assert.Equal(1, byName["Good"].Releases);
        Assert.Equal(IndexerQueryState.Failed, byName["Broken"].State);
        Assert.Equal("401 Unauthorized", byName["Broken"].Message);
        Assert.Equal(IndexerQueryState.Failed, byName["Slow"].State);
        Assert.Contains("Timed out", byName["Slow"].Message);
        Assert.All(result.Indexers, s => Assert.NotNull(s.Seconds));

        Assert.Single(result.Releases);
        Assert.Equal(["Broken", "Slow"], result.Errors.Select(e => e.IndexerName).Order());
    }

    [Fact]
    public async Task Search_without_progress_still_returns_statuses()
    {
        using var scope = _services.CreateScope();
        var service = new IndexerSearchService(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>(), new FakeTorznab(), NullLogger<IndexerSearchService>.Instance);
        var result = await service.SearchAsync(new SearchCriteria { Type = SearchType.Movie, Query = "movie" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Indexers.Count);
    }
}
