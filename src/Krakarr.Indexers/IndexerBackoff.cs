using System.Collections.Concurrent;
using Krakarr.Core.Events;
using Krakarr.Core.Health;
using Microsoft.Extensions.Logging;

namespace Krakarr.Indexers;

/// <summary>An indexer that failed recently.</summary>
/// <param name="Failures">Consecutive failed queries.</param>
/// <param name="DisabledUntil">Searches skip the indexer until then.</param>
/// <param name="LastError">Why the last query failed.</param>
public sealed record IndexerStatus(int IndexerId, string IndexerName, int Failures, DateTimeOffset DisabledUntil, DateTimeOffset LastFailure, string LastError);

/// <summary>
/// Skips indexers that keep failing, so a dead indexer doesn't slow every search and RSS sync (or get the user banned
/// for hammering it). Each consecutive failure doubles the wait, from <see cref="FirstDelay"/> up to
/// <see cref="MaxDelay"/>; any successful query, a successful Test, editing or deleting the indexer clears it.
/// Kept in memory: one entry per failing indexer, and a restart gives every indexer a fresh start.
/// </summary>
public sealed class IndexerBackoff(TimeProvider time, IEventHub events, IHealthMonitor health, ILogger<IndexerBackoff> logger)
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<int, IndexerStatus> _statuses = new();

    /// <summary>How long to skip an indexer after <paramref name="failures"/> consecutive failures: 5 min, 10, 20, … 24 h.</summary>
    public static TimeSpan DelayFor(int failures)
    {
        if (failures <= 0) return TimeSpan.Zero;
        // 5 min × 2^9 is past 24 h already; stop doubling before the shift overflows.
        var factor = 1L << Math.Min(failures - 1, 20);
        var delay = TimeSpan.FromTicks(FirstDelay.Ticks * factor);
        return delay < MaxDelay ? delay : MaxDelay;
    }

    /// <summary>Every indexer that failed its last query, whether or not its wait is over.</summary>
    public IReadOnlyList<IndexerStatus> All => _statuses.Values.OrderBy(s => s.IndexerName, StringComparer.OrdinalIgnoreCase).ToList();

    public IndexerStatus? Get(int indexerId) => _statuses.GetValueOrDefault(indexerId);

    /// <summary>True while searches should skip the indexer.</summary>
    public bool IsDisabled(int indexerId) => _statuses.TryGetValue(indexerId, out var s) && s.DisabledUntil > time.GetUtcNow();

    public void RecordFailure(int indexerId, string indexerName, string error)
    {
        var now = time.GetUtcNow();
        var status = _statuses.AddOrUpdate(indexerId,
            _ => new IndexerStatus(indexerId, indexerName, 1, now + DelayFor(1), now, error),
            (_, s) =>
            {
                // A query that started before the indexer was disabled and failed afterwards doesn't count twice.
                var failures = s.DisabledUntil > now ? s.Failures : s.Failures + 1;
                return new IndexerStatus(indexerId, indexerName, failures, now + DelayFor(failures), now, error);
            });
        logger.LogWarning("Indexer {Indexer} failed {Failures} time(s) in a row; skipping it until {Until:u}",
            indexerName, status.Failures, status.DisabledUntil);
        Changed();
    }

    /// <summary>Clears the indexer's failures (it answered, it was tested, edited or deleted).</summary>
    public void Reset(int indexerId)
    {
        if (!_statuses.TryRemove(indexerId, out var old)) return;
        logger.LogInformation("Indexer {Indexer} is working again", old.IndexerName);
        Changed();
    }

    private void Changed()
    {
        health.RequestCheck();
        _ = PublishAsync();
    }

    private async Task PublishAsync()
    {
        try
        {
            await events.PublishAsync(EventNames.IndexerStatusUpdated, null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not publish the indexer status");
        }
    }
}
