using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Indexers.Tests;

public sealed class IndexerSearchServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeTorznab _torznab = new();
    private readonly ManualTime _time = new();
    private readonly RecordingHealthMonitor _health = new();
    private readonly IndexerBackoff _backoff;
    private ServiceProvider _services = null!;

    /// <summary>"Good" returns one release, "Broken" throws, "Slow" times out (cancelled) once <see cref="Gate"/> opens.</summary>
    private sealed class FakeTorznab : ITorznabClient
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(Indexer indexer, SearchCriteria criteria, CancellationToken ct = default)
        {
            switch (indexer.Name)
            {
                case "Good":
                    return [new ReleaseInfo { Title = "Movie.2024.1080p", Guid = "g1", IndexerId = indexer.Id, IndexerName = indexer.Name }];
                case "Broken":
                    throw new IndexerException("401 Unauthorized");
                default:
                    await Gate.Task;
                    throw new TaskCanceledException();
            }
        }

        public Task<IndexerCapabilities> GetCapabilitiesAsync(Indexer indexer, bool forceRefresh = false, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    public IndexerSearchServiceTests() =>
        _backoff = new IndexerBackoff(_time, new NullEventHub(), _health, NullLogger<IndexerBackoff>.Instance);

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

    private IndexerSearchService Service(IServiceScope scope) =>
        new(scope.ServiceProvider.GetRequiredService<KrakarrDbContext>(), _torznab, _backoff, NullLogger<IndexerSearchService>.Instance);

    private static readonly SearchCriteria Criteria = new() { Type = SearchType.Movie, Query = "movie" };

    [Fact]
    public async Task Hands_out_each_indexer_result_without_waiting_for_the_slow_ones()
    {
        using var scope = _services.CreateScope();
        var ct = TestContext.Current.CancellationToken;
        var run = await Service(scope).StartAsync(Criteria, ct: ct);

        // The disabled indexer is never queried.
        Assert.Equal(["Broken", "Good", "Slow"], run.Indexers.Select(s => s.IndexerName).Order());
        Assert.All(run.Indexers, s => Assert.Equal(IndexerQueryState.Pending, s.State));

        await using var results = run.Results.GetAsyncEnumerator(ct);
        var early = new List<IndexerQueryResult>();
        for (var i = 0; i < 2; i++)
        {
            Assert.True(await results.MoveNextAsync());
            early.Add(results.Current);
        }

        // "Slow" is still blocked, yet both others have been handed out.
        var good = Assert.Single(early, r => r.Status.IndexerName == "Good");
        Assert.Equal(IndexerQueryState.Done, good.Status.State);
        Assert.Equal(1, good.Status.Releases);
        Assert.Single(good.Releases);
        var broken = Assert.Single(early, r => r.Status.IndexerName == "Broken");
        Assert.Equal(IndexerQueryState.Failed, broken.Status.State);
        Assert.Equal("401 Unauthorized", broken.Status.Message);
        Assert.Empty(broken.Releases);

        _torznab.Gate.SetResult();
        Assert.True(await results.MoveNextAsync());
        Assert.Equal("Slow", results.Current.Status.IndexerName);
        Assert.Contains("Timed out", results.Current.Status.Message);
        Assert.False(await results.MoveNextAsync());
    }

    [Fact]
    public async Task Search_waits_for_every_indexer_and_reports_their_statuses()
    {
        using var scope = _services.CreateScope();
        _torznab.Gate.SetResult();
        var result = await Service(scope).SearchAsync(Criteria, ct: TestContext.Current.CancellationToken);

        var byName = result.Indexers.ToDictionary(s => s.IndexerName);
        Assert.Equal(3, byName.Count);
        Assert.Equal(IndexerQueryState.Done, byName["Good"].State);
        Assert.Equal(IndexerQueryState.Failed, byName["Broken"].State);
        Assert.Equal(IndexerQueryState.Failed, byName["Slow"].State);
        Assert.All(result.Indexers, s => Assert.NotNull(s.Seconds));
        Assert.Single(result.Releases);
        Assert.Equal(["Broken", "Slow"], result.Errors.Select(e => e.IndexerName).Order());
    }

    [Fact]
    public async Task Search_filters_to_requested_enabled_indexers()
    {
        using var scope = _services.CreateScope();
        var ct = TestContext.Current.CancellationToken;
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var byName = await db.Indexers.AsNoTracking().ToDictionaryAsync(i => i.Name, i => i.Id, ct);

        // Single enabled indexer plus a disabled one: only the enabled one is queried.
        var single = await Service(scope).SearchAsync(Criteria, [byName["Good"], byName["Disabled"]], ct);
        Assert.Equal(["Good"], single.Indexers.Select(s => s.IndexerName));
        Assert.Single(single.Releases);
        Assert.Empty(single.Errors);

        // Multiple enabled indexers: only those two are queried ("Slow" is never touched).
        var multiple = await Service(scope).SearchAsync(Criteria, [byName["Good"], byName["Broken"]], ct);
        Assert.Equal(["Broken", "Good"], multiple.Indexers.Select(s => s.IndexerName).Order());
        Assert.Single(multiple.Releases);
        Assert.Equal(["Broken"], multiple.Errors.Select(e => e.IndexerName));
    }

    [Fact]
    public async Task Failures_start_a_backoff_and_a_success_ends_it()
    {
        using var scope = _services.CreateScope();
        var ct = TestContext.Current.CancellationToken;
        var ids = await IdsAsync(scope, ct);
        _torznab.Gate.SetResult();
        _backoff.RecordFailure(ids["Good"], "Good", "earlier failure");

        await Service(scope).SearchAsync(Criteria, ct: ct);

        // "Good" was skipped (still backing off), the two that failed now back off too.
        Assert.Equal(["Broken", "Good", "Slow"], _backoff.All.Select(s => s.IndexerName));
        Assert.Equal("401 Unauthorized", _backoff.Get(ids["Broken"])!.LastError);
        Assert.True(_backoff.IsDisabled(ids["Broken"]));

        // Once its wait is over it is queried again, and answering clears it.
        _time.Advance(IndexerBackoff.FirstDelay);
        var result = await Service(scope).SearchAsync(Criteria, ct: ct);
        Assert.Equal(IndexerQueryState.Done, result.Indexers.Single(s => s.IndexerName == "Good").State);
        Assert.Null(_backoff.Get(ids["Good"]));
        Assert.Equal(2, _backoff.Get(ids["Broken"])!.Failures);
        Assert.True(_health.Requests > 0);
    }

    [Fact]
    public async Task Backing_off_indexers_are_skipped_unless_chosen_explicitly()
    {
        using var scope = _services.CreateScope();
        var ct = TestContext.Current.CancellationToken;
        var ids = await IdsAsync(scope, ct);
        _torznab.Gate.SetResult();
        _backoff.RecordFailure(ids["Good"], "Good", "HTTP 503");

        var all = await Service(scope).SearchAsync(Criteria, ct: ct);
        var good = all.Indexers.Single(s => s.IndexerName == "Good");
        Assert.Equal(IndexerQueryState.Skipped, good.State);
        Assert.Contains("HTTP 503", good.Message);
        Assert.Empty(all.Releases);
        Assert.DoesNotContain(all.Errors, e => e.IndexerName == "Good"); // skipping isn't a new failure

        var chosen = await Service(scope).SearchAsync(Criteria, [ids["Good"]], ct);
        Assert.Equal(IndexerQueryState.Done, Assert.Single(chosen.Indexers).State);
        Assert.Single(chosen.Releases);
        Assert.False(_backoff.IsDisabled(ids["Good"]));
    }

    private static async Task<Dictionary<string, int>> IdsAsync(IServiceScope scope, CancellationToken ct) =>
        await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Indexers.AsNoTracking().ToDictionaryAsync(i => i.Name, i => i.Id, ct);
}
