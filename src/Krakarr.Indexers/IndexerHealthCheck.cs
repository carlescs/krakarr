using Krakarr.Core.Health;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Indexers;

/// <summary>
/// No enabled indexer, or indexers that keep failing (from <see cref="IndexerBackoff"/>, no network calls). Failing
/// indexers are summed up in a single "failing" problem, so the bell shows one notification however many are down.
/// </summary>
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
        var failing = backoff.All.Where(s => enabledIds.Contains(s.IndexerId)).OrderBy(s => s.IndexerName, StringComparer.OrdinalIgnoreCase).ToList();
        if (failing.Count == 0) return [];

        // Every indexer down means nothing can be found at all.
        var allDown = failing.Count(s => s.DisabledUntil > now) == enabled.Count;
        var severity = allDown ? HealthSeverity.Error : HealthSeverity.Warning;
        string message;
        if (failing.Count == 1)
        {
            var s = failing[0];
            message = $"{s.IndexerName} failed {s.Failures} time(s) in a row: {s.LastError}. "
                + (s.DisabledUntil > now ? $"Searches skip it until {s.DisabledUntil:u}." : "It is retried on the next search.");
        }
        else
        {
            var details = failing.Select(s => $"{s.IndexerName} ({s.Failures} failure(s): {s.LastError}; "
                + (s.DisabledUntil > now ? $"skipped until {s.DisabledUntil:u})" : "retried on the next search)"));
            var lead = allDown
                ? $"All {failing.Count} indexers are failing, so searches find nothing"
                : $"{failing.Count} of {enabled.Count} indexers are failing";
            message = $"{lead}: {string.Join("; ", details)}.";
        }

        return [new HealthProblem("failing", severity, message, "/indexers", failing.Count)];
    }
}
