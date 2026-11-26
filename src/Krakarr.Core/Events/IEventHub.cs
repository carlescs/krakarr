namespace Krakarr.Core.Events;

/// <summary>Pushes real-time events to connected UI clients.</summary>
public interface IEventHub
{
    Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default);
}

/// <summary>No-op hub used when no transport is registered (tests, tools).</summary>
public sealed class NullEventHub : IEventHub
{
    public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public static class EventNames
{
    public const string DownloadsUpdated = "downloadsUpdated";
    public const string MovieUpdated = "movieUpdated";
    public const string SeriesUpdated = "seriesUpdated";
    public const string SubtitlesUpdated = "subtitlesUpdated";
    public const string CommandUpdated = "commandUpdated";
    public const string HistoryAdded = "historyAdded";
    public const string TaskUpdated = "taskUpdated";
    /// <summary>Requests changed (created, approved, declined, deleted). No payload.</summary>
    public const string RequestsUpdated = "requestsUpdated";
    /// <summary>What a notification source reports may have changed; clients refetch the notifications. No payload.</summary>
    public const string NotificationsUpdated = "notificationsUpdated";
    /// <summary>A background task was queued, started, finished, cancelled or dismissed; staff refetch the list. No payload.</summary>
    public const string BackgroundTasksUpdated = "backgroundTasksUpdated";
    /// <summary>
    /// Progress of a background task changed (<c>BackgroundTaskProgress</c>); clients update it in place without
    /// refetching the list. Staff only.
    /// </summary>
    public const string BackgroundTaskProgress = "backgroundTaskProgress";
    /// <summary>A health run finished; admins refetch the report. No payload: the report holds paths, and managers get staff events too.</summary>
    public const string HealthUpdated = "healthUpdated";
    /// <summary>An indexer's failure backoff started, changed or ended. No payload.</summary>
    public const string IndexerStatusUpdated = "indexerStatusUpdated";
}
