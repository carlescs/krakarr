using Krakarr.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Krakarr.Core.Health;

/// <summary>
/// Runs every <see cref="IHealthCheck"/> every <see cref="Interval"/> and shortly after <see cref="RequestCheck"/>, keeps
/// the latest <see cref="HealthReport"/>, and tells the UI when it changes (<see cref="EventNames.HealthUpdated"/>, plus
/// <see cref="EventNames.NotificationsUpdated"/> when problems appear, go away or change severity).
/// </summary>
public sealed class HealthMonitor(
    IServiceScopeFactory scopeFactory,
    IEventHub events,
    TimeProvider time,
    ILogger<HealthMonitor> logger) : BackgroundService, IHealthMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Wait after a request before running, so a burst of requests (several indexers failing at once) runs once.</summary>
    internal TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _requested = new(0, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile HealthReport _current = HealthReport.Empty;

    public HealthReport Current => _current;

    public void RequestCheck()
    {
        try
        {
            _requested.Release();
        }
        catch (SemaphoreFullException)
        {
            // A run is already requested.
        }
    }

    public async Task<HealthReport> CheckNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await RunAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = InitialDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await _requested.WaitAsync(wait, stoppingToken)) await Task.Delay(Debounce, time, stoppingToken);
                // Requests made during the delay are covered by this run.
                while (_requested.Wait(0))
                {
                }

                await CheckNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Health checks failed");
            }

            wait = Interval;
        }
    }

    private async Task<HealthReport> RunAsync(CancellationToken ct)
    {
        var previous = _current;
        var before = previous.Issues.ToDictionary(i => i.Id);
        var now = time.GetUtcNow();
        var issues = new List<HealthIssue>();

        using (var scope = scopeFactory.CreateScope())
        {
            foreach (var check in scope.ServiceProvider.GetServices<IHealthCheck>())
            {
                IReadOnlyList<HealthProblem> problems;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(CheckTimeout);
                    problems = await check.CheckAsync(timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Health check {Check} failed", check.Id);
                    var message = ex is OperationCanceledException ? $"Timed out after {CheckTimeout.TotalSeconds:0} seconds" : ex.Message;
                    problems = [new HealthProblem("failed", HealthSeverity.Warning, $"The check could not run: {message}")];
                }

                foreach (var p in problems.DistinctBy(p => p.Key))
                {
                    var id = $"{check.Id}.{p.Key}";
                    var since = before.TryGetValue(id, out var old) && old.Severity == p.Severity ? old.Since : now;
                    issues.Add(new HealthIssue(id, check.Id, check.Name, p.Severity, p.Message, p.Link, since));
                }
            }
        }

        // Errors first, then in check order (the order the checks were registered).
        var ordered = issues.OrderByDescending(i => i.Severity).ToList();
        var report = new HealthReport(time.GetUtcNow(), ordered);
        _current = report;
        await AnnounceAsync(previous, report, ct);
        return report;
    }

    private async Task AnnounceAsync(HealthReport previous, HealthReport current, CancellationToken ct)
    {
        var before = previous.Issues.ToDictionary(i => i.Id);
        var after = current.Issues.ToDictionary(i => i.Id);
        foreach (var issue in current.Issues.Where(i => !before.TryGetValue(i.Id, out var old) || old.Severity != i.Severity))
        {
            logger.LogWarning("Health {Severity}: {CheckName}: {Message}", issue.Severity, issue.CheckName, issue.Message);
        }

        foreach (var issue in previous.Issues.Where(i => !after.ContainsKey(i.Id)))
        {
            logger.LogInformation("Health problem resolved: {CheckName}: {Message}", issue.CheckName, issue.Message);
        }

        // The bell shows the messages too, so any change to them is a notification change.
        var changed = before.Count != after.Count
            || current.Issues.Any(i => !before.TryGetValue(i.Id, out var old) || old with { Since = i.Since } != i);

        try
        {
            await events.PublishAsync(EventNames.HealthUpdated, null, ct);
            if (changed) await events.PublishAsync(EventNames.NotificationsUpdated, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not publish the health report");
        }
    }
}

public static class HealthServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrHealth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStorageProbe, StorageProbe>();
        services.TryAddSingleton<HealthValidationCache>();
        services.TryAddSingleton<HealthMonitor>();
        services.TryAddSingleton<IHealthMonitor>(sp => sp.GetRequiredService<HealthMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<HealthMonitor>());
        return services;
    }
}
