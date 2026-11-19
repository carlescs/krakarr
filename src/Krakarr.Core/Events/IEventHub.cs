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
    public const string CommandUpdated = "commandUpdated";
    public const string HistoryAdded = "historyAdded";
}
