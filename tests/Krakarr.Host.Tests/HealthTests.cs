using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Health;
using Krakarr.Core.Notifications;
using Krakarr.Host.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Krakarr.Host.Tests;

public sealed class HealthMonitorTests
{
    private readonly FakeCheck _check = new();
    private readonly RecordingEvents _events = new();
    private readonly ManualTimeProvider _time = new();

    private sealed class FakeCheck : IHealthCheck
    {
        public List<HealthProblem> Problems { get; set; } = [];
        public Exception? Failure { get; set; }
        public string Id => "fake";
        public string Name => "Fake";

        public Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default) =>
            Failure is null ? Task.FromResult<IReadOnlyList<HealthProblem>>(Problems.ToList()) : Task.FromException<IReadOnlyList<HealthProblem>>(Failure);
    }

    private sealed class RecordingEvents : IEventHub
    {
        public List<string> Published { get; } = [];

        public Task PublishAsync(string eventName, object? payload, CancellationToken cancellationToken = default)
        {
            lock (Published) Published.Add(eventName);
            return Task.CompletedTask;
        }
    }

    private HealthMonitor Monitor()
    {
        var services = new ServiceCollection();
        services.AddScoped<IHealthCheck>(_ => _check);
        return new HealthMonitor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _events, _time, NullLogger<HealthMonitor>.Instance);
    }

    [Fact]
    public async Task Problems_keep_the_time_they_appeared_until_they_get_worse()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = Monitor();
        Assert.Null(monitor.Current.CheckedAt);

        _check.Problems = [new HealthProblem("disk", HealthSeverity.Warning, "Low on space", "/settings/media")];
        var first = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal(("fake.disk", "Fake", "/settings/media"), (first.Id, first.CheckName, first.Link));
        var appeared = first.Since;

        _time.Advance(TimeSpan.FromMinutes(5));
        _check.Problems = [new HealthProblem("disk", HealthSeverity.Warning, "Lower on space")];
        var later = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal(appeared, later.Since);
        Assert.Equal("Lower on space", later.Message);

        _time.Advance(TimeSpan.FromMinutes(5));
        _check.Problems = [new HealthProblem("disk", HealthSeverity.Error, "Out of space")];
        var worse = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal(_time.GetUtcNow(), worse.Since); // notified again, even if the warning was dismissed

        _time.Advance(TimeSpan.FromMinutes(5));
        _check.Problems = [new HealthProblem("disk", HealthSeverity.Warning, "Low on space again")];
        var better = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal(worse.Since, better.Since); // easing off doesn't bring a dismissed problem back

        _check.Problems = [];
        var fixedReport = await monitor.CheckNowAsync(ct);
        Assert.Empty(fixedReport.Issues);
        Assert.Equal(_time.GetUtcNow(), fixedReport.CheckedAt);
        Assert.Same(fixedReport, monitor.Current);
    }

    [Fact]
    public async Task A_problem_standing_for_more_things_is_new_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = Monitor();

        _check.Problems = [new HealthProblem("failing", HealthSeverity.Warning, "A is failing", Count: 1)];
        var one = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal(1, one.Count);

        _time.Advance(TimeSpan.FromMinutes(5));
        _check.Problems = [new HealthProblem("failing", HealthSeverity.Warning, "A and B are failing", Count: 2)];
        var two = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal((2, _time.GetUtcNow()), (two.Count, two.Since)); // another failure: back in the bell

        _time.Advance(TimeSpan.FromMinutes(5));
        _check.Problems = [new HealthProblem("failing", HealthSeverity.Warning, "B is failing", Count: 1)];
        var fewer = Assert.Single((await monitor.CheckNowAsync(ct)).Issues);
        Assert.Equal((1, two.Since), (fewer.Count, fewer.Since)); // one recovered: stays dismissed

        var n = Assert.Single(await new HealthNotificationSource(monitor).GetAsync(new NotificationContext(1, UserRole.Admin), ct));
        Assert.Equal(1, n.Count);
    }

    [Fact]
    public async Task Tells_clients_to_refresh_the_bell_only_when_problems_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = Monitor();
        await monitor.CheckNowAsync(ct); // healthy, as before
        Assert.Equal([EventNames.HealthUpdated], _events.Published);

        _check.Problems = [new HealthProblem("x", HealthSeverity.Warning, "Broken")];
        await monitor.CheckNowAsync(ct);
        await monitor.CheckNowAsync(ct); // unchanged
        Assert.Equal(1, _events.Published.Count(e => e == EventNames.NotificationsUpdated));
        Assert.Equal(3, _events.Published.Count(e => e == EventNames.HealthUpdated));
    }

    [Fact]
    public async Task A_check_that_throws_becomes_a_warning()
    {
        _check.Failure = new InvalidOperationException("boom");
        var issue = Assert.Single((await Monitor().CheckNowAsync(TestContext.Current.CancellationToken)).Issues);
        Assert.Equal(("fake.failed", HealthSeverity.Warning), (issue.Id, issue.Severity));
        Assert.Contains("boom", issue.Message);
    }

    [Fact]
    public async Task A_requested_check_runs_without_waiting_for_the_interval()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = Monitor();
        monitor.RequestCheck();
        monitor.RequestCheck(); // coalesced
        await monitor.StartAsync(ct);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (monitor.Current.CheckedAt is null && DateTime.UtcNow < deadline) await Task.Delay(50, ct);
            Assert.NotNull(monitor.Current.CheckedAt);
        }
        finally
        {
            await monitor.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Validation_outcomes_are_remembered_per_configuration()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = new HealthValidationCache(_time);
        var calls = 0;
        Task<HealthProblem?> Validate(CancellationToken _)
        {
            calls++;
            return Task.FromResult<HealthProblem?>(new HealthProblem("key", HealthSeverity.Error, "Rejected"));
        }

        await cache.GetAsync("svc", "key-1", Validate, ct);
        await cache.GetAsync("svc", "key-1", Validate, ct);
        Assert.Equal(1, calls);

        await cache.GetAsync("svc", "key-2", Validate, ct); // new key
        Assert.Equal(2, calls);

        _time.Advance(HealthValidationCache.FailureLifetime); // failures are retried hourly
        await cache.GetAsync("svc", "key-2", Validate, ct);
        Assert.Equal(3, calls);

        cache.Clear();
        await cache.GetAsync("svc", "key-2", Validate, ct);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Only_admins_get_health_notifications()
    {
        var ct = TestContext.Current.CancellationToken;
        _check.Problems = [new HealthProblem("disk", HealthSeverity.Error, "Out of space")];
        var monitor = Monitor();
        await monitor.CheckNowAsync(ct);
        var source = new HealthNotificationSource(monitor);

        var n = Assert.Single(await source.GetAsync(new NotificationContext(1, UserRole.Admin), ct));
        Assert.Equal(("health.fake.disk", "health", "Fake", "Out of space", "/settings/health"), (n.Id, n.Kind, n.Title, n.Message, n.Link));
        Assert.Equal(NotificationSeverity.Error, n.Severity);
        Assert.Equal(monitor.Current.Issues[0].Since, n.Date);
        Assert.Empty(await source.GetAsync(new NotificationContext(2, UserRole.Manager), ct));
        Assert.Empty(await source.GetAsync(new NotificationContext(3, UserRole.User), ct));
    }
}

public sealed class HealthEndpointTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> AdminAsync()
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var ct = TestContext.Current.CancellationToken;
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Reports_problems_and_puts_them_in_the_bell()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync();

        // A fresh install: no TMDB key and no indexers.
        var report = await (await client.PostAsync("/api/v1/system/health/check", null, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var ids = report.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Contains("metadata.tmdb", ids);
        Assert.Contains("indexers.none", ids);
        Assert.NotEqual(JsonValueKind.Null, report.GetProperty("checkedAt").ValueKind);

        var current = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/health", ct);
        Assert.Equal(ids.Count, current.GetProperty("issues").GetArrayLength());
        var tmdb = current.GetProperty("issues").EnumerateArray().First(i => i.GetProperty("id").GetString() == "metadata.tmdb");
        Assert.Equal("Error", tmdb.GetProperty("severity").GetString());

        var notifications = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/notifications", ct);
        Assert.Contains(notifications!, n => n.GetProperty("id").GetString() == "health.metadata.tmdb");

        // The liveness probe stays anonymous and unchanged.
        var anonymous = await factory.CreateClient().GetAsync("/api/v1/health", ct);
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/system/health", ct)).StatusCode);
    }
}

public sealed class StorageProbeTests
{
    [Theory]
    [InlineData("/data/movies", "/data", true)]
    [InlineData("/data", "/data/", true)]
    [InlineData("/datastore/movies", "/data", false)]
    [InlineData("/anything", "/", true)]
    public void Paths_belong_to_mounts_segment_wise(string path, string root, bool expected)
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, StorageProbe.IsUnder(path, root));
    }

    [Fact]
    public void Answers_for_a_real_directory()
    {
        var dir = Directory.CreateTempSubdirectory("krakarr-probe").FullName;
        try
        {
            var probe = new StorageProbe();
            Assert.True(probe.DirectoryExists(dir));
            Assert.False(probe.HasEntries(dir));
            File.WriteAllText(Path.Combine(dir, "a"), "x");
            Assert.True(probe.HasEntries(dir));
            Assert.True(probe.IsWritable(dir));

            var space = probe.GetSpace(dir);
            Assert.NotNull(space);
            Assert.True(space.TotalBytes > 0);
            Assert.True(StorageProbe.IsUnder(dir, space.MountPoint));
            Assert.False(probe.DirectoryExists(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
