using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Krakarr.Subtitles.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Subtitles.Tests;

public sealed class SubtitleSyncProgressTests
{
    private readonly BackgroundTasks _tasks = new(new NullEventHub(), TimeProvider.System, NullLogger<BackgroundTasks>.Instance);
    private readonly SubtitleSyncProgress _progress;

    public SubtitleSyncProgressTests() => _progress = new SubtitleSyncProgress(_tasks);

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
    public void Reports_a_sync_as_a_background_task()
    {
        var token = _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        var waiting = Assert.Single(_tasks.Current);
        Assert.Equal("subtitles.sync.7", waiting.Id);
        Assert.Equal(SubtitleSyncProgress.Kind, waiting.Kind);
        Assert.Equal("Sync Dune (2021) · English", waiting.Title);
        Assert.Equal("/movies/3", waiting.Link);
        Assert.Equal(BackgroundTaskState.Waiting, waiting.State);

        _progress.Stage(7, "Syncing with alass against the audio");
        _progress.ReportLine(7, " 42%|████▏     |");
        var running = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Running, running.State);
        Assert.Equal("Syncing with alass against the audio", running.Message);
        Assert.Equal(42, running.Percent);

        Assert.True(_tasks.Cancel(SubtitleSyncProgress.TaskId(7))); // the tasks menu's Cancel button
        Assert.True(token.IsCancellationRequested);
        _progress.Complete(7, false, SubtitleSyncProgress.CancelledResult, cancelled: true);
        _progress.Finish(7);
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(_tasks.Current).State);
    }

    [Fact]
    public void A_sync_asked_for_by_a_user_is_theirs_and_an_automatic_one_is_for_staff()
    {
        _progress.Queue(1, "Dune (2021) · English", "/movies/3", ownerId: 5);
        _progress.Queue(2, "Severance S01E03 · Spanish", "/series/5");

        Assert.Equal(["subtitles.sync.1"], _tasks.For(new BackgroundTaskViewer(5, IsStaff: false)).Select(t => t.Id));
        Assert.Equal(["subtitles.sync.2"], _tasks.For(new BackgroundTaskViewer(6, IsStaff: true)).Select(t => t.Id));
    }

    [Fact]
    public void A_sync_that_ends_without_an_outcome_disappears()
    {
        _progress.Queue(7, "Dune (2021) · English", "/movies/3");
        _progress.Finish(7);
        Assert.Empty(_tasks.Current);
    }
}
