using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Health;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Indexers.Tests;

public sealed class IndexerBackoffTests
{
    private readonly ManualTime _time = new();
    private readonly RecordingHealthMonitor _health = new();
    private readonly IndexerBackoff _backoff;

    public IndexerBackoffTests() => _backoff = new IndexerBackoff(_time, new NullEventHub(), _health, NullLogger<IndexerBackoff>.Instance);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(9, 1280)]
    [InlineData(10, 1440)] // 2560 min, capped at 24 h
    [InlineData(1000, 1440)]
    public void Delay_doubles_from_five_minutes_up_to_a_day(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), IndexerBackoff.DelayFor(failures));

    [Fact]
    public void Consecutive_failures_back_off_longer_until_reset()
    {
        _backoff.RecordFailure(7, "Tracker", "HTTP 500");
        Assert.True(_backoff.IsDisabled(7));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(5), _backoff.Get(7)!.DisabledUntil);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_backoff.IsDisabled(7));
        _backoff.RecordFailure(7, "Tracker", "HTTP 502");
        var status = _backoff.Get(7)!;
        Assert.Equal(2, status.Failures);
        Assert.Equal("HTTP 502", status.LastError);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(10), status.DisabledUntil);

        _backoff.Reset(7);
        Assert.Null(_backoff.Get(7));
        Assert.False(_backoff.IsDisabled(7));
        Assert.Equal(3, _health.Requests); // each change asks for a health run
    }

    [Fact]
    public void A_failure_while_disabled_does_not_count_twice()
    {
        _backoff.RecordFailure(1, "A", "first");
        _backoff.RecordFailure(1, "A", "query started before the backoff");
        Assert.Equal(1, _backoff.Get(1)!.Failures);
    }

    [Fact]
    public void Resetting_an_indexer_that_never_failed_changes_nothing()
    {
        _backoff.Reset(42);
        Assert.Equal(0, _health.Requests);
    }

    [Fact]
    public async Task Health_check_reports_failing_indexers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var db = new KrakarrDbContext(new DbContextOptionsBuilder<KrakarrDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var check = new IndexerHealthCheck(db, _backoff, _time);

        // No indexer at all.
        var none = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("none", HealthSeverity.Warning), (none.Key, none.Severity));

        var a = new Indexer { Name = "A", BaseUrl = "http://a" };
        var b = new Indexer { Name = "B", BaseUrl = "http://b" };
        db.Indexers.AddRange(a, b);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));

        // One of two failing: a warning; all of them: an error, since nothing can be found. Either way a single
        // problem, so the bell shows one notification for every failing indexer.
        _backoff.RecordFailure(a.Id, "A", "timeout");
        var one = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("failing", HealthSeverity.Warning, 1), (one.Key, one.Severity, one.Count));
        Assert.Contains("A failed 1 time(s) in a row: timeout", one.Message);
        Assert.Equal("/indexers", one.Link);

        _backoff.RecordFailure(b.Id, "B", "HTTP 401");
        var both = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("failing", HealthSeverity.Error, 2), (both.Key, both.Severity, both.Count));
        Assert.StartsWith("All 2 indexers are failing", both.Message);
        Assert.Contains("A (1 failure(s): timeout;", both.Message);
        Assert.Contains("B (1 failure(s): HTTP 401;", both.Message);
    }
}
