using System.Collections.Concurrent;
using Krakarr.Core.Events;
using Microsoft.Extensions.Logging;

namespace Krakarr.Core.Jobs;

public enum BackgroundTaskState
{
    /// <summary>Queued behind other work.</summary>
    Waiting,
    Running,
    /// <summary>Cancel was asked for and the work is stopping.</summary>
    Cancelling,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Long-running work shown in the tasks menu of the top bar, e.g. a subtitle sync.</summary>
/// <param name="Id">Stable id, unique across kinds (e.g. "subtitles.sync.12").</param>
/// <param name="Kind">What it is, for the UI's icon (e.g. "subtitle").</param>
/// <param name="Title">What is worked on, e.g. "Sync Dune (2021) · English".</param>
/// <param name="Link">SPA route of what it's about, if any.</param>
/// <param name="Message">What it's doing now while running; the outcome once finished.</param>
/// <param name="Percent">Progress of the current stage (0 to 100), null when unknown.</param>
/// <param name="RemainingSeconds">Estimated time left in the current stage, null until there is enough progress to tell.</param>
/// <param name="CanCancel">Whether it can be cancelled right now.</param>
public sealed record BackgroundTaskInfo(
    string Id,
    string Kind,
    string Title,
    string? Link,
    BackgroundTaskState State,
    string? Message,
    double? Percent,
    double? RemainingSeconds,
    DateTimeOffset Queued,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    bool CanCancel)
{
    public bool IsFinished => Finished is not null;
}

/// <summary>Payload of <see cref="EventNames.BackgroundTaskProgress"/>: new progress and message of the task <paramref name="Id"/>.</summary>
public sealed record BackgroundTaskProgress(string Id, double? Percent, string? Message, double? RemainingSeconds = null);

/// <summary>
/// Long-running work waiting, running or recently finished, for the tasks menu in the top bar (staff only). The work
/// itself runs wherever it likes (the command queue, a scheduled task); it reports here with <see cref="Queue"/>,
/// <see cref="Stage"/>, <see cref="Report"/> and <see cref="Complete"/>, and watches the token <see cref="Queue"/> returns
/// for <see cref="Cancel"/>. Finished tasks stay for <see cref="KeepFinished"/> (at most <see cref="MaxFinished"/>) so the
/// outcome can be seen, unless dismissed. The list changing publishes <see cref="EventNames.BackgroundTasksUpdated"/>;
/// progress in between goes out as <see cref="EventNames.BackgroundTaskProgress"/>, at most a few times a second per task.
/// Everything is in memory and bounded by <see cref="MaxTracked"/>.
/// </summary>
public sealed class BackgroundTasks(IEventHub eventHub, TimeProvider time, ILogger<BackgroundTasks> logger)
{
    /// <summary>Tasks tracked at once; more still run, just not shown.</summary>
    public const int MaxTracked = 200;

    /// <summary>Finished tasks kept to show their outcome; older ones are dropped first.</summary>
    public const int MaxFinished = 50;

    public static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    public const string CancelledMessage = "Cancelled before it finished";
    public const string CancellingMessage = "Cancelling…";

    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>No estimate before the stage has run this long and got this far: early rates are mostly start-up cost.</summary>
    private static readonly TimeSpan MinElapsedForEstimate = TimeSpan.FromSeconds(3);
    private const double MinPercentForEstimate = 1;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Everything tracked, oldest first.</summary>
    public IReadOnlyList<BackgroundTaskInfo> Current
    {
        get
        {
            Prune();
            return _entries.Values.Select(e => e.Snapshot()).OrderBy(t => t.Queued).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Shows a task as waiting. Does nothing when it's already waiting or running (a queued task that starts); replaces
    /// the outcome of an earlier run with the same id. Returns the token <see cref="Cancel"/> trips for this run
    /// (<see cref="CancellationToken.None"/> when it isn't tracked).
    /// </summary>
    public CancellationToken Queue(string id, string kind, string title, string? link, bool cancellable = true)
    {
        Prune();
        var entry = new Entry(id, kind, title, link, cancellable, time.GetUtcNow());
        if (_entries.TryGetValue(id, out var existing))
        {
            if (!existing.IsFinished) return existing.Token;
            if (!_entries.TryUpdate(id, entry, existing)) return CancellationToken.None;
        }
        else if (_entries.Count >= MaxTracked || !_entries.TryAdd(id, entry))
        {
            return CancellationToken.None;
        }

        Publish(EventNames.BackgroundTasksUpdated, null);
        return entry.Token;
    }

    /// <summary>
    /// Cancels a waiting or running task. A waiting one is finished as cancelled at once; a running one shows
    /// <see cref="CancellingMessage"/> until the work stops and calls <see cref="Complete"/>. False when there is nothing
    /// to cancel.
    /// </summary>
    public bool Cancel(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return false;
        lock (entry)
        {
            if (!entry.Cancellable || entry.Finished is not null || entry.CancelRequested) return false;
            entry.CancelRequested = true;
            if (entry.Started is null)
            {
                entry.Finished = time.GetUtcNow();
                entry.Outcome = BackgroundTaskState.Cancelled;
                entry.Message = CancelledMessage;
            }
            else
            {
                entry.Message = CancellingMessage;
                entry.Percent = null;
                entry.RemainingSeconds = null;
            }
        }

        entry.CancelWork();
        Publish(EventNames.BackgroundTasksUpdated, null);
        return true;
    }

    /// <summary>Moves a task to a new stage with unknown progress; stages report their own progress with <see cref="Report"/>.</summary>
    public void Stage(string id, string stage)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        bool started;
        lock (entry)
        {
            if (entry.Finished is not null || entry.CancelRequested) return;
            started = entry.Started is null;
            var now = time.GetUtcNow();
            entry.Started ??= now;
            entry.Message = stage;
            entry.Percent = null;
            entry.RemainingSeconds = null;
            entry.StageStarted = entry.LastPublished = now;
        }

        // Waiting → running changes what the list shows (state, cancel); later stages only change the message.
        if (started) Publish(EventNames.BackgroundTasksUpdated, null);
        else Publish(EventNames.BackgroundTaskProgress, new BackgroundTaskProgress(id, null, stage));
    }

    /// <summary>Progress of the current stage. Published when the whole percent changes, at most every <see cref="MinPublishInterval"/>.</summary>
    public void Report(string id, double percent)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        percent = Math.Clamp(percent, 0, 100);
        BackgroundTaskProgress update;
        lock (entry)
        {
            if (entry.Started is null || entry.Finished is not null || entry.CancelRequested) return;
            var now = time.GetUtcNow();
            var changed = entry.Percent is not { } last || Math.Floor(last) != Math.Floor(percent);
            entry.Percent = percent;
            entry.RemainingSeconds = Estimate(now - entry.StageStarted, percent);
            if (!changed || now - entry.LastPublished < MinPublishInterval) return;
            entry.LastPublished = now;
            update = new BackgroundTaskProgress(id, Math.Floor(percent), entry.Message, entry.RemainingSeconds);
        }

        Publish(EventNames.BackgroundTaskProgress, update);
    }

    /// <summary>
    /// Seconds left in a stage that took <paramref name="elapsed"/> to reach <paramref name="percent"/>, assuming it keeps
    /// the same pace. Null too early to tell.
    /// </summary>
    public static double? Estimate(TimeSpan elapsed, double percent)
    {
        if (percent >= 100) return 0;
        if (percent < MinPercentForEstimate || elapsed < MinElapsedForEstimate) return null;
        return Math.Ceiling(elapsed.TotalSeconds * (100 - percent) / percent);
    }

    /// <summary>Records the outcome, which stays for <see cref="KeepFinished"/> or until dismissed.</summary>
    public void Complete(string id, bool success, string message, bool cancelled = false)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry)
        {
            if (entry.Finished is not null) return;
            entry.Finished = time.GetUtcNow();
            entry.Outcome = cancelled ? BackgroundTaskState.Cancelled : success ? BackgroundTaskState.Succeeded : BackgroundTaskState.Failed;
            entry.Message = message;
            entry.Percent = success && !cancelled ? 100 : entry.Percent;
            entry.RemainingSeconds = null;
        }

        Prune();
        Publish(EventNames.BackgroundTasksUpdated, null);
    }

    /// <summary>Stops showing a task that ended without an outcome (e.g. what it worked on is gone). Keeps finished ones.</summary>
    public void Finish(string id)
    {
        if (_entries.TryGetValue(id, out var entry) && !entry.IsFinished
            && _entries.TryRemove(new KeyValuePair<string, Entry>(id, entry)))
            Publish(EventNames.BackgroundTasksUpdated, null);
    }

    /// <summary>Hides a finished task. False when there is none, or it is still going (cancel it instead).</summary>
    public bool Dismiss(string id)
    {
        if (!_entries.TryGetValue(id, out var entry) || !entry.IsFinished
            || !_entries.TryRemove(new KeyValuePair<string, Entry>(id, entry))) return false;
        Publish(EventNames.BackgroundTasksUpdated, null);
        return true;
    }

    /// <summary>Hides every finished task; returns how many.</summary>
    public int DismissFinished()
    {
        var removed = 0;
        foreach (var entry in _entries.Values.Where(e => e.IsFinished))
            if (_entries.TryRemove(new KeyValuePair<string, Entry>(entry.Id, entry))) removed++;
        if (removed > 0) Publish(EventNames.BackgroundTasksUpdated, null);
        return removed;
    }

    /// <summary>Drops finished tasks older than <see cref="KeepFinished"/>, and the oldest beyond <see cref="MaxFinished"/>.</summary>
    private void Prune()
    {
        var cutoff = time.GetUtcNow() - KeepFinished;
        var finished = _entries.Values.Select(e => (Entry: e, At: e.FinishedAt)).Where(x => x.At is not null)
            .OrderByDescending(x => x.At).ToList();
        for (var i = 0; i < finished.Count; i++)
        {
            var (entry, at) = finished[i];
            if (i >= MaxFinished || at < cutoff) _entries.TryRemove(new KeyValuePair<string, Entry>(entry.Id, entry));
        }
    }

    /// <summary>Fire and forget: progress comes from tools' output callbacks, which must not wait on SignalR.</summary>
    private void Publish(string eventName, object? payload)
    {
        _ = PublishCoreAsync(eventName, payload);
    }

    private async Task PublishCoreAsync(string eventName, object? payload)
    {
        try
        {
            await eventHub.PublishAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not publish {Event}", eventName);
        }
    }

    private sealed class Entry(string id, string kind, string title, string? link, bool cancellable, DateTimeOffset queued)
    {
        public string Id => id;
        public bool Cancellable => cancellable;
        public DateTimeOffset? Started { get; set; }
        public DateTimeOffset? Finished { get; set; }
        public BackgroundTaskState Outcome { get; set; }
        public string? Message { get; set; }
        public bool CancelRequested { get; set; }
        public double? Percent { get; set; }
        public double? RemainingSeconds { get; set; }
        public DateTimeOffset StageStarted { get; set; }
        public DateTimeOffset LastPublished { get; set; }

        // Not disposed: without CancelAfter it holds no timer, and dropping it is enough.
        private readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;
        public void CancelWork() => _cancellation.Cancel();

        public DateTimeOffset? FinishedAt
        {
            get { lock (this) return Finished; }
        }

        public bool IsFinished => FinishedAt is not null;

        public BackgroundTaskInfo Snapshot()
        {
            lock (this)
            {
                var state = Finished is not null ? Outcome
                    : CancelRequested ? BackgroundTaskState.Cancelling
                    : Started is null ? BackgroundTaskState.Waiting
                    : BackgroundTaskState.Running;
                var canCancel = cancellable && state is BackgroundTaskState.Waiting or BackgroundTaskState.Running;
                return new BackgroundTaskInfo(id, kind, title, link, state, Message, Percent is { } p ? Math.Floor(p) : null, RemainingSeconds,
                    queued, Started, Finished, canCancel);
            }
        }
    }
}
