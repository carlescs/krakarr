using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Krakarr.Core.Events;
using Krakarr.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles.Sync;

/// <summary>A subtitle waiting for or going through a sync, as shown in the notification bell.</summary>
/// <param name="Title">What is synced, e.g. "Dune (2021) · English".</param>
/// <param name="Link">SPA route of the movie or series.</param>
/// <param name="Stage">What it's doing now; null while waiting.</param>
/// <param name="Percent">Progress of the current stage (0 to 100), null when unknown.</param>
/// <param name="RemainingSeconds">Estimated time left in the current stage, null until there is enough progress to tell.</param>
public sealed record SubtitleSyncActivity(int SubtitleId, string Title, string Link, DateTimeOffset Since, string? Stage, double? Percent,
    double? RemainingSeconds = null)
{
    public bool Waiting => Stage is null;
}

/// <summary>
/// Syncs waiting or running, for the notification bell. Start and end publish <see cref="EventNames.NotificationsUpdated"/>;
/// progress in between goes out as <see cref="EventNames.NotificationProgress"/>, at most a few times a second per sync,
/// so open bells update in place without refetching every notification source.
/// </summary>
public sealed partial class SubtitleSyncProgress(IEventHub eventHub, TimeProvider time, ILogger<SubtitleSyncProgress> logger)
{
    /// <summary>Syncs tracked at once; more are still synced, just not shown. Syncs run one at a time, so this only caps the queue.</summary>
    internal const int MaxTracked = 200;

    private static readonly TimeSpan MinPublishInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>No estimate before the stage has run this long and got this far: early rates are mostly start-up cost.</summary>
    private static readonly TimeSpan MinElapsedForEstimate = TimeSpan.FromSeconds(3);
    private const double MinPercentForEstimate = 1;

    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    public static string NotificationId(int subtitleId) => $"subtitles.sync.{subtitleId}";

    /// <summary>Everything tracked, oldest first.</summary>
    public IReadOnlyList<SubtitleSyncActivity> Current =>
        _entries.Values.Select(e => e.Snapshot()).OrderBy(a => a.Since).ThenBy(a => a.SubtitleId).ToList();

    /// <summary>Shows a sync as waiting. Does nothing when it's already tracked (a queued sync that starts).</summary>
    public void Queue(int subtitleId, string title, string link)
    {
        if (_entries.Count >= MaxTracked) return;
        if (_entries.TryAdd(subtitleId, new Entry(subtitleId, title, link, time.GetUtcNow())))
            Publish(EventNames.NotificationsUpdated, null);
    }

    /// <summary>Moves a sync to a new stage with unknown progress; stages report their own progress with <see cref="Report"/>.</summary>
    public void Stage(int subtitleId, string stage)
    {
        if (!_entries.TryGetValue(subtitleId, out var entry)) return;
        lock (entry)
        {
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
            if (entry.Stage is null) return;
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

    public void Finish(int subtitleId)
    {
        if (_entries.TryRemove(subtitleId, out _)) Publish(EventNames.NotificationsUpdated, null);
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
        public string? Stage { get; set; }
        public double? Percent { get; set; }
        public double? RemainingSeconds { get; set; }
        public DateTimeOffset StageStarted { get; set; }
        public DateTimeOffset LastPublished { get; set; }

        public SubtitleSyncActivity Snapshot()
        {
            lock (this) return new SubtitleSyncActivity(subtitleId, title, link, since, Stage, Percent is { } p ? Math.Floor(p) : null, RemainingSeconds);
        }
    }
}

/// <summary>Staff: one notification per subtitle sync that is waiting or running, with its progress.</summary>
public sealed class SubtitleSyncNotificationSource(SubtitleSyncProgress progress) : INotificationSource
{
    public Task<IReadOnlyList<Notification>> GetAsync(NotificationContext context, CancellationToken ct = default)
    {
        if (!context.IsStaff) return Task.FromResult<IReadOnlyList<Notification>>([]);
        IReadOnlyList<Notification> notifications = progress.Current
            .Select(a => new Notification(SubtitleSyncProgress.NotificationId(a.SubtitleId), "subtitle",
                (a.Waiting ? "Waiting to sync " : "Syncing ") + a.Title,
                a.Stage ?? "Waiting for its turn", a.Link,
                Date: a.Since, Progress: new NotificationProgress(a.Percent, a.RemainingSeconds)))
            .ToList();
        return Task.FromResult(notifications);
    }
}
