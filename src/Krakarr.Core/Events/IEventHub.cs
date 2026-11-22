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
    /// <summary>Per-indexer progress of an interactive search: <c>{ searchId, indexers }</c>, keyed by the id the client passed. Staff only.</summary>
    public const string SearchProgress = "searchProgress";
}
