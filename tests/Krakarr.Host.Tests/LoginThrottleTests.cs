using Krakarr.Host.Auth;
using Microsoft.Extensions.Options;

namespace Krakarr.Host.Tests;

public sealed class LoginThrottleTests
{
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly LoginThrottle _throttle;

    public LoginThrottleTests() => _throttle = new LoginThrottle(Options.Create(new AuthSecurityOptions()), _time);

    [Fact]
    public void Locks_after_five_failures_with_exponential_backoff_capped_at_fifteen_minutes()
    {
        for (var i = 0; i < 4; i++) _throttle.RecordFailure("admin");
        Assert.Null(_throttle.RetryAfter("admin"));

        _throttle.RecordFailure("admin");
        Assert.Equal(TimeSpan.FromSeconds(30), _throttle.RetryAfter("ADMIN"));

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(_throttle.RetryAfter("admin"));
        _throttle.RecordFailure("admin");
        Assert.Equal(TimeSpan.FromMinutes(1), _throttle.RetryAfter("admin"));

        for (var i = 0; i < 20; i++) _throttle.RecordFailure("admin");
        Assert.Equal(TimeSpan.FromMinutes(15), _throttle.RetryAfter("admin"));
    }

    [Fact]
    public void Success_resets_and_usernames_are_independent()
    {
        for (var i = 0; i < 5; i++) _throttle.RecordFailure("admin");
        Assert.Null(_throttle.RetryAfter("someone-else"));
        Assert.NotNull(_throttle.RetryAfter("admin"));

        _time.Advance(TimeSpan.FromMinutes(1));
        _throttle.RecordSuccess("admin");
        _throttle.RecordFailure("admin");
        Assert.Null(_throttle.RetryAfter("admin"));
    }
}
