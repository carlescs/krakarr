using Krakarr.Core.Health;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Indexers;

/// <summary>No enabled indexer, or indexers that keep failing (from <see cref="IndexerBackoff"/>, no network calls).</summary>
public sealed class IndexerHealthCheck(KrakarrDbContext db, IndexerBackoff backoff, TimeProvider time) : IHealthCheck
{
    public string Id => "indexers";
    public string Name => "Indexers";

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var enabled = await db.Indexers.AsNoTracking().Where(i => i.Enabled).Select(i => i.Id).ToListAsync(ct);
        if (enabled.Count == 0)
        {
            return [new HealthProblem("none", HealthSeverity.Warning, "No indexer is enabled, so searches and RSS syncs find nothing.", "/indexers")];
        }

        var now = time.GetUtcNow();
        var enabledIds = enabled.ToHashSet();
        var failing = backoff.All.Where(s => enabledIds.Contains(s.IndexerId)).ToList();
        if (failing.Count == 0) return [];

        // Every indexer down means nothing can be found at all.
        var severity = failing.Count(s => s.DisabledUntil > now) == enabled.Count ? HealthSeverity.Error : HealthSeverity.Warning;
        return failing
            .Select(s => new HealthProblem(
                s.IndexerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                severity,
                $"{s.IndexerName} failed {s.Failures} time(s) in a row: {s.LastError}. "
                + (s.DisabledUntil > now ? $"Searches skip it until {s.DisabledUntil:u}." : "It is retried on the next search."),
                "/indexers"))
            .ToList();
    }
}
