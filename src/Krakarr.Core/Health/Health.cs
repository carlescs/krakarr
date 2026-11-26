namespace Krakarr.Core.Health;

public enum HealthSeverity
{
    Warning = 1,
    Error = 2,
}

/// <summary>A problem a health check found.</summary>
/// <param name="Key">Identifies the problem within its check and stays the same while it persists (e.g. the path of a root folder).</param>
/// <param name="Link">SPA route where the user can fix it, e.g. "/indexers".</param>
/// <param name="Count">How many things the problem stands for when it sums up several (e.g. failing indexers), so the
/// bell counts them and a dismissed problem comes back when more of them fail.</param>
/// <param name="NotifyEvery">For problems that come and go (indexers that fail, recover and fail again): the bell
/// brings the problem back at most once per this interval, however often it reappears or gets worse in between.</param>
public sealed record HealthProblem(string Key, HealthSeverity Severity, string Message, string? Link = null, int Count = 1,
    TimeSpan? NotifyEvery = null);

/// <summary>
/// One aspect of the setup that can be broken without anything failing loudly (disk space, root folders, the download
/// client, indexers, API keys). Register implementations with <c>services.AddScoped&lt;IHealthCheck, ...&gt;()</c>;
/// <see cref="HealthMonitor"/> runs them all every few minutes and whenever <see cref="IHealthMonitor.RequestCheck"/> is
/// called, so they must be cheap: aggregate queries, <see cref="IStorageProbe"/>, state kept in memory. A check that
/// needs the network (validating an API key) must cache its answer (see <see cref="HealthValidationCache"/>).
/// </summary>
public interface IHealthCheck
{
    /// <summary>Stable id, part of each problem's id (e.g. "rootfolders").</summary>
    string Id { get; }

    /// <summary>Shown above the check's problems, e.g. "Root folders".</summary>
    string Name { get; }

    /// <summary>The problems found; empty when everything is fine.</summary>
    Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default);
}

/// <summary>A problem as reported by the API and the notification bell.</summary>
/// <param name="Id">"{check id}.{problem key}", stable while the problem persists.</param>
/// <param name="Since">When it was first seen, or last got worse (more severe, or a higher <paramref name="Count"/>). With
/// <paramref name="NotifyEvery"/>, it moves at most once per that interval, so the bell brings the problem back no more
/// often.</param>
/// <param name="Count">See <see cref="HealthProblem.Count"/>.</param>
/// <param name="NotifyEvery">See <see cref="HealthProblem.NotifyEvery"/>.</param>
public sealed record HealthIssue(string Id, string Check, string CheckName, HealthSeverity Severity, string Message, string? Link,
    DateTimeOffset Since, int Count = 1, TimeSpan? NotifyEvery = null);

/// <summary>Result of the latest health run.</summary>
/// <param name="CheckedAt">When the last run finished; null before the first one.</param>
public sealed record HealthReport(DateTimeOffset? CheckedAt, IReadOnlyList<HealthIssue> Issues)
{
    public static readonly HealthReport Empty = new(null, []);
}

public interface IHealthMonitor
{
    /// <summary>The latest report; never blocks.</summary>
    HealthReport Current { get; }

    /// <summary>
    /// Asks for a run soon (after a short delay that coalesces bursts), e.g. after settings were saved or an indexer
    /// failed. Never blocks.
    /// </summary>
    void RequestCheck();

    /// <summary>Runs every check now and returns the new report.</summary>
    Task<HealthReport> CheckNowAsync(CancellationToken ct = default);
}
