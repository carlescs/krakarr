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
