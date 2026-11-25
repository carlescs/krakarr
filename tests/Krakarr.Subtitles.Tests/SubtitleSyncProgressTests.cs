using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Notifications;
using Krakarr.Subtitles.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Subtitles.Tests;

public sealed class SubtitleSyncProgressTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHub : IEventHub
    {
        public List<(string Name, object? Payload)> Events { get; } = [];

        public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
        {
            lock (Events) Events.Add((eventName, payload));
            return Task.CompletedTask;
        }

        public List<NotificationProgressUpdate> Updates() =>
            Events.Where(e => e.Name == EventNames.NotificationProgress).Select(e => (NotificationProgressUpdate)e.Payload!).ToList();
    }

    private readonly ManualClock _clock = new();
    private readonly RecordingHub _hub = new();
    private readonly SubtitleSyncProgress _progress;

    public SubtitleSyncProgressTests() => _progress = new SubtitleSyncProgress(_hub, _clock, NullLogger<SubtitleSyncProgress>.Instance);

    [Theory]
    [InlineData(" 45%|████▌     | 123/273 [00:05<00:06, 22.7it/s]", 45)]
    [InlineData("123 / 273 [=====>------] 45.05 % 22.70/s 6s", 45.05)]
    [InlineData("extracting audio: 7,5 %", 7.5)]
    [InlineData("100%|██████████| 273/273", 100)]
    public void Reads_the_percentage_from_tool_output(string line, double expected)
    {
        Assert.Equal(expected, SubtitleSyncProgress.ParsePercent(line)!.Value, 3);
    }

    [Theory]
    [InlineData("extracting audio from reference file")]
    [InlineData("offset 250%")]
    [InlineData("v2.0.1%")]
    public void Ignores_lines_without_a_progress_percentage(string line)
    {
        Assert.Null(SubtitleSyncProgress.ParsePercent(line));
    }

    [Fact]
    public void Tracks_a_sync_from_queued_to_finished()
    {
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        var waiting = Assert.Single(_progress.Current);
        Assert.True(waiting.Waiting);

        _progress.Stage(7, "Syncing with alass against the audio");
        _progress.ReportLine(7, " 42%|████▏     |");
        var running = Assert.Single(_progress.Current);
        Assert.False(running.Waiting);
        Assert.Equal(42, running.Percent);

        _progress.Finish(7);
        Assert.Empty(_progress.Current);
        Assert.Equal(2, _hub.Events.Count(e => e.Name == EventNames.NotificationsUpdated));
    }

    [Fact]
    public void Queuing_a_tracked_sync_again_keeps_it_as_it_is()
    {
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        _progress.Stage(7, "Looking for a reference");
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        Assert.Equal("Looking for a reference", Assert.Single(_progress.Current).Stage);
        Assert.Single(_hub.Events, e => e.Name == EventNames.NotificationsUpdated);
    }

    [Fact]
    public void Throttles_progress_events()
    {
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        _progress.Stage(7, "Syncing");
        _clock.Now += TimeSpan.FromSeconds(1);
        _progress.Report(7, 10.2);
        _progress.Report(7, 10.8); // same whole percent
        _progress.Report(7, 20);   // too soon
        _clock.Now += TimeSpan.FromSeconds(1);
        _progress.Report(7, 30);

        var updates = _hub.Updates();
        Assert.Equal([null, 10, 30], updates.Select(u => u.Percent));
        Assert.All(updates, u => Assert.Equal("subtitles.sync.7", u.Id));
    }

    [Theory]
    [InlineData(10, 25, 30)]     // a quarter in 10 s: 30 s to go
    [InlineData(20, 50, 20)]
    [InlineData(7, 33.3, 15)]    // rounded up
    [InlineData(60, 100, 0)]
    public void Estimates_the_time_left_at_the_same_pace(int elapsedSeconds, double percent, double expected)
    {
        Assert.Equal(expected, SubtitleSyncProgress.Estimate(TimeSpan.FromSeconds(elapsedSeconds), percent));
    }

    [Theory]
    [InlineData(2, 50)]   // too soon
    [InlineData(30, 0.5)] // too little done
    public void Gives_no_estimate_too_early(int elapsedSeconds, double percent)
    {
        Assert.Null(SubtitleSyncProgress.Estimate(TimeSpan.FromSeconds(elapsedSeconds), percent));
    }

    [Fact]
    public async Task Reports_the_time_left_in_the_current_stage()
    {
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        _clock.Now += TimeSpan.FromMinutes(5); // waiting doesn't count
        _progress.Stage(7, "Syncing with alass against the audio");
        _clock.Now += TimeSpan.FromSeconds(20);
        _progress.Report(7, 40);

        Assert.Equal(30, Assert.Single(_progress.Current).RemainingSeconds);
        Assert.Equal(30, _hub.Updates()[^1].RemainingSeconds);
        var shown = await new SubtitleSyncNotificationSource(_progress)
            .GetAsync(new NotificationContext(1, UserRole.Admin), TestContext.Current.CancellationToken);
        Assert.Equal(new NotificationProgress(40, 30), Assert.Single(shown).Progress);

        _progress.Stage(7, "Next stage");
        Assert.Null(Assert.Single(_progress.Current).RemainingSeconds);
        Assert.Null(_hub.Updates()[^1].RemainingSeconds);
    }

    [Fact]
    public async Task A_finished_sync_shows_green_when_it_worked_and_red_when_it_did_not()
    {
        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Queue(2, "Severance S01E03 · Spanish", "/series/5");
        _progress.Stage(1, "Syncing with alass against the audio");
        _progress.Report(1, 60);
        _clock.Now += TimeSpan.FromMinutes(2);
        _progress.Complete(1, true, "Synced with alass against the audio");
        _progress.Complete(2, false, "The video file was not found");
        _progress.Finish(1); // what callers do after: keeps the outcome

        var shown = await new SubtitleSyncNotificationSource(_progress)
            .GetAsync(new NotificationContext(1, UserRole.Manager), TestContext.Current.CancellationToken);
        Assert.Collection(shown,
            n =>
            {
                Assert.Equal("subtitles.sync.1", n.Id);
                Assert.Equal("Synced Dune (2021) · English", n.Title);
                Assert.Equal("Synced with alass against the audio", n.Message);
                Assert.Equal(NotificationSeverity.Success, n.Severity);
                Assert.Equal(new NotificationProgress(100), n.Progress);
                Assert.Equal(_clock.Now, n.Date);
            },
            n =>
            {
                Assert.Equal("Could not sync Severance S01E03 · Spanish", n.Title);
                Assert.Equal("The video file was not found", n.Message);
                Assert.Equal(NotificationSeverity.Error, n.Severity);
                Assert.Null(n.Progress);
            });
    }

    [Fact]
    public void A_finished_sync_ignores_late_progress_and_is_replaced_by_a_new_sync()
    {
        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Complete(1, false, "boom");
        _progress.Stage(1, "late");
        _progress.Report(1, 50);
        Assert.Null(Assert.Single(_progress.Current).Stage);

        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        var again = Assert.Single(_progress.Current);
        Assert.True(again.Waiting);
        Assert.Null(again.Finished);
    }

    [Fact]
    public void Finished_syncs_expire_and_are_capped()
    {
        _progress.Queue(1, "Old", "/movies/1");
        _progress.Complete(1, true, "Synced");
        _clock.Now += SubtitleSyncProgress.KeepFinished + TimeSpan.FromSeconds(1);
        Assert.Empty(_progress.Current);

        for (var i = 0; i < SubtitleSyncProgress.MaxFinished + 5; i++)
        {
            _progress.Queue(i, $"Sub {i}", "/subtitles");
            _clock.Now += TimeSpan.FromSeconds(1);
            _progress.Complete(i, true, "Synced");
        }

        var kept = _progress.Current;
        Assert.Equal(SubtitleSyncProgress.MaxFinished, kept.Count);
        Assert.DoesNotContain(kept, a => a.SubtitleId < 5); // the oldest went first
    }

    [Fact]
    public async Task Running_and_waiting_syncs_have_a_cancel_button()
    {
        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Queue(2, "Severance S01E03 · Spanish", "/series/5");
        _progress.Stage(1, "Syncing");
        _progress.Queue(3, "Old", "/movies/1");
        _progress.Complete(3, true, "Synced");

        var shown = await new SubtitleSyncNotificationSource(_progress)
            .GetAsync(new NotificationContext(1, UserRole.Manager), TestContext.Current.CancellationToken);
        Assert.Equal([new NotificationAction("Cancel", "subtitles/1/sync/cancel")], shown.Single(n => n.Id == "subtitles.sync.1").Actions!);
        Assert.Equal([new NotificationAction("Cancel", "subtitles/2/sync/cancel")], shown.Single(n => n.Id == "subtitles.sync.2").Actions!);
        Assert.Null(shown.Single(n => n.Id == "subtitles.sync.3").Actions);
    }

    [Fact]
    public async Task Cancelling_a_waiting_sync_finishes_it_at_once()
    {
        var token = _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        Assert.Equal(token, _progress.Queue(1, "Dune (2021) · English", "/movies/3")); // the queued sync starting

        Assert.True(_progress.Cancel(1));
        Assert.True(token.IsCancellationRequested);
        Assert.False(_progress.Cancel(1));
        _progress.Stage(1, "late");
        _progress.Complete(1, false, "The operation was canceled", cancelled: true);

        var n = Assert.Single(await new SubtitleSyncNotificationSource(_progress)
            .GetAsync(new NotificationContext(1, UserRole.Admin), TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled sync of Dune (2021) · English", n.Title);
        Assert.Equal(SubtitleSyncProgress.CancelledResult, n.Message);
        Assert.Equal(NotificationSeverity.Info, n.Severity);
        Assert.Null(n.Actions);
        Assert.Null(n.Progress);
    }

    [Fact]
    public async Task Cancelling_a_running_sync_shows_it_stopping_until_it_completes()
    {
        var token = _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Stage(1, "Syncing with alass against the audio");
        _progress.Report(1, 40);

        Assert.True(_progress.Cancel(1));
        Assert.True(token.IsCancellationRequested);
        _progress.Report(1, 60); // the tool's last lines before it is killed
        var source = new SubtitleSyncNotificationSource(_progress);
        var stopping = Assert.Single(await source.GetAsync(new NotificationContext(1, UserRole.Admin), TestContext.Current.CancellationToken));
        Assert.Equal("Cancelling…", stopping.Message);
        Assert.Equal(new NotificationProgress(null), stopping.Progress);
        Assert.Null(stopping.Actions);

        _progress.Complete(1, false, SubtitleSyncProgress.CancelledResult, cancelled: true);
        var done = Assert.Single(_progress.Current);
        Assert.True(done.Cancelled);
        Assert.False(done.Succeeded);
        Assert.False(done.Cancelling);
    }

    [Fact]
    public void Nothing_to_cancel_once_finished_or_untracked()
    {
        Assert.False(_progress.Cancel(9));
        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Complete(1, true, "Synced");
        Assert.False(_progress.Cancel(1));
        Assert.True(Assert.Single(_progress.Current).Succeeded);
    }

    [Fact]
    public void Untracked_syncs_report_nothing()
    {
        _progress.Stage(9, "Syncing");
        _progress.Report(9, 50);
        _progress.Finish(9);
        Assert.Empty(_hub.Events);
    }

    [Fact]
    public void Caps_how_many_syncs_are_tracked()
    {
        for (var i = 0; i < SubtitleSyncProgress.MaxTracked + 10; i++) _progress.Queue(i, $"Sub {i}", "/subtitles");
        Assert.Equal(SubtitleSyncProgress.MaxTracked, _progress.Current.Count);
    }

    [Fact]
    public async Task Shows_syncs_to_staff_only_with_their_progress()
    {
        _progress.Queue(1, "Dune (2021) · English", "/movies/3");
        _progress.Queue(2, "Severance S01E03 · Spanish", "/series/5");
        _progress.Stage(1, "Syncing with alass against the audio");
        var source = new SubtitleSyncNotificationSource(_progress);

        Assert.Empty(await source.GetAsync(new NotificationContext(3, UserRole.User), TestContext.Current.CancellationToken));
        var shown = await source.GetAsync(new NotificationContext(1, UserRole.Manager), TestContext.Current.CancellationToken);
        Assert.Collection(shown,
            n =>
            {
                Assert.Equal("subtitles.sync.1", n.Id);
                Assert.Equal("Syncing Dune (2021) · English", n.Title);
                Assert.Equal("Syncing with alass against the audio", n.Message);
                Assert.Equal("/movies/3", n.Link);
                Assert.NotNull(n.Progress);
                Assert.Null(n.Progress.Percent);
            },
            n =>
            {
                Assert.Equal("Waiting to sync Severance S01E03 · Spanish", n.Title);
                Assert.Equal("/series/5", n.Link);
            });
    }
}
