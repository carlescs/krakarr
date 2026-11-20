using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Events;
using Krakarr.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Host.Tests;

public sealed class ScheduledTaskRunnerTests
{
    private sealed class Probe
    {
        public int Runs;
        public TaskCompletionSource Ran { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Gate { get; set; }
        public TimeSpan? Interval { get; set; } = TimeSpan.FromHours(1);
    }

    private sealed class ProbeTask(Probe probe) : IScheduledTask
    {
        public string Name => "Probe";
        public TimeSpan Interval => TimeSpan.FromHours(1);
        public TimeSpan InitialDelay => TimeSpan.FromHours(1);
        public ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken) => ValueTask.FromResult(probe.Interval);

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref probe.Runs);
            if (probe.Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            probe.Ran.TrySetResult();
        }
    }

    private static (ScheduledTaskRunner Runner, Probe Probe) Create()
    {
        var probe = new Probe();
        var services = new ServiceCollection()
            .AddSingleton(probe)
            .AddScheduledTask<ProbeTask>()
            .BuildServiceProvider();
        var runner = new ScheduledTaskRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetServices<ScheduledTaskRegistration>(),
            new NullEventHub(),
            NullLogger<ScheduledTaskRunner>.Instance);
        return (runner, probe);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public async Task Runs_a_task_on_demand_and_records_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, probe) = Create();
        await runner.StartAsync(ct);
        try
        {
            var info = Assert.Single(runner.GetTasks());
            Assert.Equal("Probe", info.Id);
            Assert.Null(info.LastRun);

            Assert.Equal(TaskTriggerResult.Started, runner.Trigger("probe"));
            await probe.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await WaitUntilAsync(() => runner.GetTasks()[0] is { IsRunning: false, LastSucceeded: true });

            info = runner.GetTasks()[0];
            Assert.Equal(1, probe.Runs);
            Assert.NotNull(info.LastRun);
            Assert.Equal(info.LastRun + TimeSpan.FromHours(1), info.NextRun);
            Assert.Equal(TaskTriggerResult.NotFound, runner.Trigger("missing"));
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Refuses_a_second_run_while_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, probe) = Create();
        probe.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await runner.StartAsync(ct);
        try
        {
            Assert.Equal(TaskTriggerResult.Started, runner.Trigger("Probe"));
            await WaitUntilAsync(() => runner.GetTasks()[0].IsRunning);
            Assert.Equal(TaskTriggerResult.AlreadyRunning, runner.Trigger("Probe"));

            probe.Gate.SetResult();
            await probe.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await WaitUntilAsync(() => !runner.GetTasks()[0].IsRunning);
            Assert.Equal(1, probe.Runs);
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Disabled_tasks_have_no_next_run_but_can_run_manually()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, probe) = Create();
        probe.Interval = null;
        await runner.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => runner.GetTasks()[0] is { NextRun: null, IntervalSeconds: null });
            Assert.Equal(TaskTriggerResult.Started, runner.Trigger("Probe"));
            await probe.Ran.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await WaitUntilAsync(() => runner.GetTasks()[0] is { IsRunning: false, LastSucceeded: true, NextRun: null });
        }
        finally
        {
            await runner.StopAsync(CancellationToken.None);
        }
    }
}

public sealed class ScheduledTaskEndpointTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> LoggedInClientAsync()
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var ct = TestContext.Current.CancellationToken;
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Lists_tasks_and_rejects_unknown_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await LoggedInClientAsync();

        var tasks = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/system/tasks", ct);
        Assert.NotNull(tasks);
        var refresh = Assert.Single(tasks, t => t.GetProperty("id").GetString() == "MovieRefresh");
        Assert.Equal("Refresh movie metadata", refresh.GetProperty("name").GetString());
        Assert.Equal(TimeSpan.FromHours(24).TotalSeconds, refresh.GetProperty("intervalSeconds").GetDouble());
        Assert.Contains(tasks, t => t.GetProperty("id").GetString() == "SeriesRssSync");

        var missing = await client.PostAsync("/api/v1/system/tasks/Nope/run", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
