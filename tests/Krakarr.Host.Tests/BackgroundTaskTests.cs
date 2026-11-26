using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Host.Tests;

public sealed class BackgroundTasksTests
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

        public int ListUpdates => Events.Count(e => e.Name == EventNames.BackgroundTasksUpdated);

        public List<BackgroundTaskProgress> Progress() =>
            Events.Where(e => e.Name == EventNames.BackgroundTaskProgress).Select(e => (BackgroundTaskProgress)e.Payload!).ToList();
    }

    private readonly ManualClock _clock = new();
    private readonly RecordingHub _hub = new();
    private readonly BackgroundTasks _tasks;

    public BackgroundTasksTests() => _tasks = new BackgroundTasks(_hub, _clock, NullLogger<BackgroundTasks>.Instance);

    private CancellationToken Queue(string id, string title = "Sync Dune (2021) · English", string? link = "/movies/3") =>
        _tasks.Queue(id, "subtitle", title, link);

    [Fact]
    public void Tracks_a_task_from_waiting_to_finished()
    {
        Queue("a");
        var waiting = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Waiting, waiting.State);
        Assert.Equal("subtitle", waiting.Kind);
        Assert.Equal("/movies/3", waiting.Link);
        Assert.True(waiting.CanCancel);
        Assert.Null(waiting.Started);

        _clock.Now += TimeSpan.FromMinutes(1);
        _tasks.Stage("a", "Syncing with alass against the audio");
        _tasks.Report("a", 42.7);
        var running = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Running, running.State);
        Assert.Equal(_clock.Now, running.Started);
        Assert.Equal("Syncing with alass against the audio", running.Message);
        Assert.Equal(42, running.Percent);

        _tasks.Complete("a", true, "Synced with alass against the audio");
        var done = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Succeeded, done.State);
        Assert.Equal("Synced with alass against the audio", done.Message);
        Assert.Equal(100, done.Percent);
        Assert.Equal(_clock.Now, done.Finished);
        Assert.False(done.CanCancel);

        // Queued, started, finished: the list changed three times; the percentage went out as progress.
        Assert.Equal(3, _hub.ListUpdates);
    }

    [Fact]
    public void A_failed_task_keeps_its_last_progress()
    {
        Queue("a");
        _tasks.Stage("a", "Syncing");
        _tasks.Report("a", 60);
        _tasks.Complete("a", false, "The video file was not found");
        var failed = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Failed, failed.State);
        Assert.Equal("The video file was not found", failed.Message);
        Assert.Equal(60, failed.Percent);
    }

    [Fact]
    public void Queuing_a_tracked_task_again_keeps_it_as_it_is()
    {
        var token = Queue("a");
        _tasks.Stage("a", "Looking for a reference");
        Assert.Equal(token, Queue("a")); // the queued work starting
        Assert.Equal("Looking for a reference", Assert.Single(_tasks.Current).Message);
        Assert.Equal(2, _hub.ListUpdates);
    }

    [Fact]
    public void Throttles_progress_events()
    {
        Queue("a");
        _tasks.Stage("a", "Syncing");
        _tasks.Stage("a", "Still syncing");
        _clock.Now += TimeSpan.FromSeconds(1);
        _tasks.Report("a", 10.2);
        _tasks.Report("a", 10.8); // same whole percent
        _tasks.Report("a", 20);   // too soon
        _clock.Now += TimeSpan.FromSeconds(1);
        _tasks.Report("a", 30);

        var updates = _hub.Progress();
        Assert.Equal([null, 10, 30], updates.Select(u => u.Percent));
        Assert.Equal("Still syncing", updates[0].Message);
        Assert.All(updates, u => Assert.Equal("a", u.Id));
    }

    [Theory]
    [InlineData(10, 25, 30)]     // a quarter in 10 s: 30 s to go
    [InlineData(20, 50, 20)]
    [InlineData(7, 33.3, 15)]    // rounded up
    [InlineData(60, 100, 0)]
    public void Estimates_the_time_left_at_the_same_pace(int elapsedSeconds, double percent, double expected)
    {
        Assert.Equal(expected, BackgroundTasks.Estimate(TimeSpan.FromSeconds(elapsedSeconds), percent));
    }

    [Theory]
    [InlineData(2, 50)]   // too soon
    [InlineData(30, 0.5)] // too little done
    public void Gives_no_estimate_too_early(int elapsedSeconds, double percent)
    {
        Assert.Null(BackgroundTasks.Estimate(TimeSpan.FromSeconds(elapsedSeconds), percent));
    }

    [Fact]
    public void Reports_the_time_left_in_the_current_stage()
    {
        Queue("a");
        _clock.Now += TimeSpan.FromMinutes(5); // waiting doesn't count
        _tasks.Stage("a", "Syncing with alass against the audio");
        _clock.Now += TimeSpan.FromSeconds(20);
        _tasks.Report("a", 40);

        Assert.Equal(30, Assert.Single(_tasks.Current).RemainingSeconds);
        Assert.Equal(30, _hub.Progress()[^1].RemainingSeconds);

        _tasks.Stage("a", "Next stage");
        Assert.Null(Assert.Single(_tasks.Current).RemainingSeconds);
        Assert.Null(_hub.Progress()[^1].RemainingSeconds);
    }

    [Fact]
    public void A_finished_task_ignores_late_progress_and_is_replaced_by_a_new_run()
    {
        Queue("a");
        _tasks.Complete("a", false, "boom");
        _tasks.Stage("a", "late");
        _tasks.Report("a", 50);
        Assert.Equal("boom", Assert.Single(_tasks.Current).Message);

        Queue("a");
        var again = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Waiting, again.State);
        Assert.Null(again.Finished);
    }

    [Fact]
    public void Finished_tasks_expire_and_are_capped()
    {
        Queue("old");
        _tasks.Complete("old", true, "Synced");
        _clock.Now += BackgroundTasks.KeepFinished + TimeSpan.FromSeconds(1);
        Assert.Empty(_tasks.Current);

        for (var i = 0; i < BackgroundTasks.MaxFinished + 5; i++)
        {
            Queue($"t{i:D3}");
            _clock.Now += TimeSpan.FromSeconds(1);
            _tasks.Complete($"t{i:D3}", true, "Synced");
        }

        var kept = _tasks.Current;
        Assert.Equal(BackgroundTasks.MaxFinished, kept.Count);
        Assert.DoesNotContain(kept, t => string.CompareOrdinal(t.Id, "t005") < 0); // the oldest went first
    }

    [Fact]
    public void Cancelling_a_waiting_task_finishes_it_at_once()
    {
        var token = Queue("a");
        Assert.True(_tasks.Cancel("a"));
        Assert.True(token.IsCancellationRequested);
        Assert.False(_tasks.Cancel("a"));
        _tasks.Stage("a", "late");
        _tasks.Complete("a", false, "The operation was canceled", cancelled: true);

        var t = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Cancelled, t.State);
        Assert.Equal(BackgroundTasks.CancelledMessage, t.Message);
        Assert.False(t.CanCancel);
        Assert.Null(t.Percent);
    }

    [Fact]
    public void Cancelling_a_running_task_shows_it_stopping_until_it_completes()
    {
        var token = Queue("a");
        _tasks.Stage("a", "Syncing with alass against the audio");
        _tasks.Report("a", 40);

        Assert.True(_tasks.Cancel("a"));
        Assert.True(token.IsCancellationRequested);
        _tasks.Report("a", 60); // the tool's last lines before it is killed
        var stopping = Assert.Single(_tasks.Current);
        Assert.Equal(BackgroundTaskState.Cancelling, stopping.State);
        Assert.Equal(BackgroundTasks.CancellingMessage, stopping.Message);
        Assert.Null(stopping.Percent);
        Assert.False(stopping.CanCancel);

        _tasks.Complete("a", false, BackgroundTasks.CancelledMessage, cancelled: true);
        Assert.Equal(BackgroundTaskState.Cancelled, Assert.Single(_tasks.Current).State);
    }

    [Fact]
    public void Tasks_queued_as_not_cancellable_cannot_be_cancelled()
    {
        var token = _tasks.Queue("a", "other", "Rebuild", null, cancellable: false);
        Assert.False(Assert.Single(_tasks.Current).CanCancel);
        Assert.False(_tasks.Cancel("a"));
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Nothing_to_cancel_once_finished_or_untracked()
    {
        Assert.False(_tasks.Cancel("nope"));
        Queue("a");
        _tasks.Complete("a", true, "Synced");
        Assert.False(_tasks.Cancel("a"));
        Assert.Equal(BackgroundTaskState.Succeeded, Assert.Single(_tasks.Current).State);
    }

    [Fact]
    public void Finish_drops_unfinished_tasks_only()
    {
        Queue("a");
        Queue("b");
        _tasks.Complete("b", true, "Synced");
        _tasks.Finish("a");
        _tasks.Finish("b");
        Assert.Equal("b", Assert.Single(_tasks.Current).Id);
    }

    [Fact]
    public void Dismisses_finished_tasks_but_not_running_ones()
    {
        Queue("a");
        Queue("b");
        Queue("c");
        _tasks.Complete("b", true, "Synced");
        _tasks.Complete("c", false, "boom");

        Assert.False(_tasks.Dismiss("a"));
        Assert.True(_tasks.Dismiss("b"));
        Assert.False(_tasks.Dismiss("b"));
        Assert.Equal(["a", "c"], _tasks.Current.Select(t => t.Id));

        Assert.Equal(1, _tasks.DismissFinished());
        Assert.Equal("a", Assert.Single(_tasks.Current).Id);
        Assert.Equal(0, _tasks.DismissFinished());
    }

    [Fact]
    public void Untracked_tasks_report_nothing()
    {
        _tasks.Stage("nope", "Syncing");
        _tasks.Report("nope", 50);
        _tasks.Complete("nope", true, "Synced");
        _tasks.Finish("nope");
        Assert.Empty(_hub.Events);
    }

    [Fact]
    public void Caps_how_many_tasks_are_tracked()
    {
        for (var i = 0; i < BackgroundTasks.MaxTracked + 10; i++) Queue($"t{i}");
        Assert.Equal(BackgroundTasks.MaxTracked, _tasks.Current.Count);
    }
}

public sealed class BackgroundTaskEndpointTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", Ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Lists_cancels_and_dismisses_tasks()
    {
        var client = await LoggedInClientAsync();
        var tasks = factory.Services.GetRequiredService<BackgroundTasks>();
        tasks.DismissFinished();
        var token = tasks.Queue("test.endpoint.1", "subtitle", "Sync Dune (2021) · English", "/movies/3");
        tasks.Queue("test.endpoint.2", "subtitle", "Sync Severance S01E03 · Spanish", "/series/5");
        tasks.Stage("test.endpoint.2", "Syncing");
        tasks.Report("test.endpoint.2", 25);

        var listed = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/system/background-tasks", Ct);
        var first = Assert.Single(listed!, t => t.GetProperty("id").GetString() == "test.endpoint.1");
        Assert.Equal("Waiting", first.GetProperty("state").GetString());
        Assert.True(first.GetProperty("canCancel").GetBoolean());
        var second = Assert.Single(listed!, t => t.GetProperty("id").GetString() == "test.endpoint.2");
        Assert.Equal("Running", second.GetProperty("state").GetString());
        Assert.Equal(25, second.GetProperty("percent").GetDouble());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/v1/system/background-tasks/test.endpoint.1/cancel", null, Ct)).StatusCode);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/system/background-tasks/test.endpoint.1/cancel", null, Ct)).StatusCode);

        // Still running: can't be dismissed, only cancelled.
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/system/background-tasks/test.endpoint.2", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/system/background-tasks/test.endpoint.1", Ct)).StatusCode);

        tasks.Complete("test.endpoint.2", true, "Synced");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/system/background-tasks", Ct)).StatusCode);
        Assert.DoesNotContain(tasks.Current, t => t.Id.StartsWith("test.endpoint.", StringComparison.Ordinal));
    }
}
