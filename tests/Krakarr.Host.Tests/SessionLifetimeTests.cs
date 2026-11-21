using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Host.Tests;

public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Host with a controllable clock and a 20-day absolute session lifetime (14-day idle timeout).</summary>
public sealed class SessionLifetimeFactory : KrakarrFactory
{
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Krakarr:Auth:SessionMaxAge", "20.00:00:00");
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }
}

public sealed class SessionLifetimeTests(SessionLifetimeFactory factory) : IClassFixture<SessionLifetimeFactory>
{
    [Fact]
    public async Task Active_sessions_still_expire_after_the_absolute_lifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient(new() { HandleCookies = true });
        var setup = await client.PostAsJsonAsync("/api/v1/auth/setup",
            new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct);
        Assert.Equal(HttpStatusCode.NoContent, setup.StatusCode);

        // Used every 12 days: the idle timeout never hits, but 24 days after signing in the session is over.
        factory.Clock.Advance(TimeSpan.FromDays(12));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/movies", ct)).StatusCode);
        factory.Clock.Advance(TimeSpan.FromDays(12));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/movies", ct)).StatusCode);

        // Signing in again starts a new lifetime.
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "admin", password = "correct-horse" }, ct);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/movies", ct)).StatusCode);
    }
}
