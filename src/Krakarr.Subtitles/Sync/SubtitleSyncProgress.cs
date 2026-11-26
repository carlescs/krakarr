using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Krakarr.Core.Events;
using Krakarr.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles.Sync;

/// <summary>A subtitle waiting for, going through or just done with a sync, as shown in the subtitle sync tray of the top bar.</summary>
/// <param name="Title">What is synced, e.g. "Dune (2021) · English".</param>
/// <param name="Link">SPA route of the movie or series.</param>
/// <param name="Stage">What it's doing now; null while waiting.</param>
/// <param name="Percent">Progress of the current stage (0 to 100), null when unknown.</param>
/// <param name="RemainingSeconds">Estimated time left in the current stage, null until there is enough progress to tell.</param>
/// <param name="Finished">When the sync ended, null while waiting or running.</param>
/// <param name="Succeeded">Whether it worked, once finished.</param>
/// <param name="Result">The outcome, once finished (e.g. "Synced with alass against the audio").</param>
/// <param name="Cancelled">Finished because it was cancelled.</param>
/// <param name="Cancelling">Cancel was asked for and the sync is stopping.</param>
public sealed record SubtitleSyncActivity(int SubtitleId, string Title, string Link, DateTimeOffset Since, string? Stage, double? Percent,
    double? RemainingSeconds = null, DateTimeOffset? Finished = null, bool Succeeded = false, string? Result = null,
    bool Cancelled = false, bool Cancelling = false)
{
    public bool Waiting => Stage is null && Finished is null;
}

/// <summary>
/// Syncs waiting, running or recently finished, for the subtitle sync tray in the top bar. Finished syncs stay for
/// <see cref="KeepFinished"/> (at most <see cref="MaxFinished"/> of them) so the outcome can be seen. Start and end publish
/// <see cref="EventNames.NotificationsUpdated"/>;
/// progress in between goes out as <see cref="EventNames.NotificationProgress"/>, at most a few times a second per sync,
/// so open trays update in place without refetching every notification source.
/// </summary>
public sealed partial class SubtitleSyncProgress(IEventHub eventHub, TimeProvider time, ILogger<SubtitleSyncProgress> logger)
{
    /// <summary>Syncs tracked at once; more are still synced, just not shown. Syncs run one at a time, so this only caps the queue.</summary>
    internal const int MaxTracked = 200;

    /// <summary>Finished syncs kept to show their outcome; older ones are dropped first.</summary>
    internal const int MaxFinished = 50;

    internal static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>No estimate before the stage has run this long and got this far: early rates are mostly start-up cost.</summary>
    private static readonly TimeSpan MinElapsedForEstimate = TimeSpan.FromSeconds(3);
    private const double MinPercentForEstimate = 1;

    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    public static string NotificationId(int subtitleId) => $"subtitles.sync.{subtitleId}";

    /// <summary>Everything tracked, oldest first.</summary>
    public IReadOnlyList<SubtitleSyncActivity> Current
    {
        get
        {
            Prune();
            return _entries.Values.Select(e => e.Snapshot()).OrderBy(a => a.Since).ThenBy(a => a.SubtitleId).ToList();
        }
    }

    /// <summary>
    /// Shows a sync as waiting. Does nothing when it's already waiting or running (a queued sync that starts); replaces
    /// the outcome of an earlier sync of the same subtitle. Returns the token <see cref="Cancel"/> trips for this sync
    /// (<see cref="CancellationToken.None"/> when it isn't tracked).
    /// </summary>
    public CancellationToken Queue(int subtitleId, string title, string link)
    {
        Prune();
        var entry = new Entry(subtitleId, title, link, time.GetUtcNow());
        if (_entries.TryGetValue(subtitleId, out var existing))
        {
            if (!existing.IsFinished) return existing.Token;
            if (!_entries.TryUpdate(subtitleId, entry, existing)) return CancellationToken.None;
        }
        else if (_entries.Count >= MaxTracked || !_entries.TryAdd(subtitleId, entry))
        {
            return CancellationToken.None;
        }

        Publish(EventNames.NotificationsUpdated, null);
        return entry.Token;
    }

    /// <summary>
    /// Cancels a waiting or running sync. A waiting one is finished as cancelled at once; a running one shows
    /// "Cancelling…" until the sync stops and calls <see cref="Complete"/>. False when there is nothing to cancel.
    /// </summary>
    public bool Cancel(int subtitleId)
    {
        if (!_entries.TryGetValue(subtitleId, out var entry)) return false;
        bool waiting;
        lock (entry)
        {
            if (entry.Finished is not null || entry.CancelRequested) return false;
            entry.CancelRequested = true;
            waiting = entry.Stage is null;
            if (waiting)
            {
                entry.Finished = time.GetUtcNow();
                entry.Cancelled = true;
                entry.Result = CancelledResult;
            }
            else
            {
                entry.Stage = "Cancelling…";
                entry.Percent = null;
                entry.RemainingSeconds = null;
            }
        }

        entry.CancelSync();
        Publish(EventNames.NotificationsUpdated, null);
        return true;
    }

    public const string CancelledResult = "Cancelled before it finished";

    /// <summary>Moves a sync to a new stage with unknown progress; stages report their own progress with <see cref="Report"/>.</summary>
    public void Stage(int subtitleId, string stage)
    {
        if (!_entries.TryGetValue(subtitleId, out var entry)) return;
        lock (entry)
        {
            if (entry.Finished is not null || entry.CancelRequested) return;
            entry.Stage = stage;
            entry.Percent = null;
            entry.RemainingSeconds = null;
            entry.StageStarted = entry.LastPublished = time.GetUtcNow();
        }

        Publish(EventNames.NotificationProgress, new NotificationProgressUpdate(NotificationId(subtitleId), null, stage));
    }

    /// <summary>Progress of the current stage. Published when the whole percent changes, at most every <see cref="MinPublishInterval"/>.</summary>
    public void Report(int subtitleId, double percent)
    {
        if (!_entries.TryGetValue(subtitleId, out var entry)) return;
        percent = Math.Clamp(percent, 0, 100);
        NotificationProgressUpdate update;
        lock (entry)
        {
            if (entry.Stage is null || entry.Finished is not null || entry.CancelRequested) return;
            var now = time.GetUtcNow();
            var changed = entry.Percent is not { } last || Math.Floor(last) != Math.Floor(percent);
            entry.Percent = percent;
            entry.RemainingSeconds = Estimate(now - entry.StageStarted, percent);
            if (!changed || now - entry.LastPublished < MinPublishInterval) return;
            entry.LastPublished = now;
            update = new NotificationProgressUpdate(NotificationId(subtitleId), Math.Floor(percent), entry.Stage, entry.RemainingSeconds);
        }

        Publish(EventNames.NotificationProgress, update);
    }

    /// <summary>Reports the percentage found in a line a sync tool printed, if any.</summary>
    public void ReportLine(int subtitleId, string line)
    {
        if (ParsePercent(line) is { } percent) Report(subtitleId, percent);
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

    /// <summary>Records the outcome: the notification turns green (or red on failure) and stays for <see cref="KeepFinished"/>.</summary>
    public void Complete(int subtitleId, bool success, string result, bool cancelled = false)
    {
        if (!_entries.TryGetValue(subtitleId, out var entry)) return;
        lock (entry)
        {
            if (entry.Finished is not null) return;
            entry.Finished = time.GetUtcNow();
            entry.Succeeded = success && !cancelled;
            entry.Cancelled = cancelled;
            entry.Result = result;
            entry.Percent = success ? 100 : entry.Percent;
            entry.RemainingSeconds = null;
        }

        Prune();
        Publish(EventNames.NotificationsUpdated, null);
    }

    /// <summary>Stops showing a sync that ended without an outcome (cancelled, or the subtitle is gone). Keeps finished ones.</summary>
    public void Finish(int subtitleId)
    {
        if (_entries.TryGetValue(subtitleId, out var entry) && !entry.IsFinished
            && _entries.TryRemove(new KeyValuePair<int, Entry>(subtitleId, entry)))
            Publish(EventNames.NotificationsUpdated, null);
    }

    /// <summary>Drops finished syncs older than <see cref="KeepFinished"/>, and the oldest beyond <see cref="MaxFinished"/>.</summary>
    private void Prune()
    {
        var cutoff = time.GetUtcNow() - KeepFinished;
        var finished = _entries.Values.Select(e => (Entry: e, At: e.FinishedAt)).Where(x => x.At is not null)
            .OrderByDescending(x => x.At).ToList();
        for (var i = 0; i < finished.Count; i++)
        {
            var (entry, at) = finished[i];
            if (i >= MaxFinished || at < cutoff) _entries.TryRemove(new KeyValuePair<int, Entry>(entry.SubtitleId, entry));
        }
    }

    /// <summary>
    /// The last percentage in a progress line, as printed by ffsubsync (tqdm: " 45%|████▌ | 123/273") or alass
    /// ("123 / 273 [====>----] 45.05 %"). Null when the line has none.
    /// </summary>
    public static double? ParsePercent(string line)
    {
        var matches = PercentPattern().Matches(line);
        if (matches.Count == 0) return null;
        var value = double.Parse(matches[^1].Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        return value is >= 0 and <= 100 ? value : null;
    }

    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[.,]\d+)?)\s?%")]
    private static partial Regex PercentPattern();

    /// <summary>Fire and forget: progress comes from the tool's output callbacks, which must not wait on SignalR.</summary>
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

    private sealed class Entry(int subtitleId, string title, string link, DateTimeOffset since)
    {
        public int SubtitleId => subtitleId;
        public string? Stage { get; set; }
        public DateTimeOffset? Finished { get; set; }
        public bool Succeeded { get; set; }
        public string? Result { get; set; }
        public bool Cancelled { get; set; }
        public bool CancelRequested { get; set; }

        // Not disposed: without CancelAfter it holds no timer, and dropping it is enough.
        private readonly CancellationTokenSource _cancellation = new();
        public CancellationToken Token => _cancellation.Token;
        public void CancelSync() => _cancellation.Cancel();

        public DateTimeOffset? FinishedAt
        {
            get { lock (this) return Finished; }
        }

        public bool IsFinished => FinishedAt is not null;
        public double? Percent { get; set; }
        public double? RemainingSeconds { get; set; }
        public DateTimeOffset StageStarted { get; set; }
        public DateTimeOffset LastPublished { get; set; }

        public SubtitleSyncActivity Snapshot()
        {
            lock (this)
                return new SubtitleSyncActivity(subtitleId, title, link, since, Stage, Percent is { } p ? Math.Floor(p) : null, RemainingSeconds,
                    Finished, Succeeded, Result, Cancelled, CancelRequested && Finished is null);
        }
    }
}

/// <summary>
/// Staff: one notification per subtitle sync that is waiting or running, with its progress, then with its outcome: green
/// when it worked, red when it didn't. They go to their own tray (<see cref="NotificationTrays.SubtitleSync"/>), not the bell.
/// </summary>
public sealed class SubtitleSyncNotificationSource(SubtitleSyncProgress progress) : INotificationSource
{
    public Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        if (!context.IsStaff) return Task.FromResult<IReadOnlyList<Notification>>([]);
        IReadOnlyList<Notification> notifications = progress.Current.Select(a => ToNotification(a) with { Tray = NotificationTrays.SubtitleSync }).ToList();
        return Task.FromResult(notifications);
    }

    private static Notification ToNotification(SubtitleSyncActivity a)
    {
        var id = SubtitleSyncProgress.NotificationId(a.SubtitleId);
        // Dated when it ended, so a sync dismissed while running shows again with its outcome.
        if (a.Finished is { } finished)
        {
            if (a.Cancelled) return new Notification(id, "subtitle", "Cancelled sync of " + a.Title, a.Result, a.Link, Date: finished);
            return a.Succeeded
                ? new Notification(id, "subtitle", "Synced " + a.Title, a.Result, a.Link,
                    Severity: NotificationSeverity.Success, Date: finished, Progress: new NotificationProgress(100))
                : new Notification(id, "subtitle", "Could not sync " + a.Title, a.Result, a.Link,
                    Severity: NotificationSeverity.Error, Date: finished);
        }

        IReadOnlyList<NotificationAction>? actions = a.Cancelling ? null : [new NotificationAction("Cancel", $"subtitles/{a.SubtitleId}/sync/cancel")];
        return new Notification(id, "subtitle", (a.Waiting ? "Waiting to sync " : "Syncing ") + a.Title,
            a.Stage ?? "Waiting for its turn", a.Link, Date: a.Since, Progress: new NotificationProgress(a.Percent, a.RemainingSeconds),
            Actions: actions);
    }
}
