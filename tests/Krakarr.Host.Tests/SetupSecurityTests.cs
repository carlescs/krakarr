using System.Net;
using System.Net.Http.Json;

namespace Krakarr.Host.Tests;

/// <summary>Host that generates its own setup token (no Krakarr:SetupToken), like a real first run.</summary>
public sealed class GeneratedTokenFactory : KrakarrFactory
{
    protected override string? SetupToken => null;
}

/// <summary>Host with a tiny per-IP limit on the credential endpoints.</summary>
public sealed class StrictRateLimitFactory : KrakarrFactory
{
    protected override int AuthAttemptsPerMinutePerIp => 3;
}

public sealed class SetupTokenTests(GeneratedTokenFactory factory) : IClassFixture<GeneratedTokenFactory>
{
    [Fact]
    public async Task Setup_requires_the_generated_one_time_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient(new() { HandleCookies = true });
        var tokenFile = Path.Combine(factory.ConfigDirectory, "setup-token");
        Assert.True(File.Exists(tokenFile));
        var token = File.ReadAllText(tokenFile).Trim();
        Assert.Equal(32, token.Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
        }

        var none = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = "admin", password = "correct-horse" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        var wrong = await client.PostAsJsonAsync("/api/v1/auth/setup", new { username = "admin", password = "correct-horse", setupToken = "guess" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        // Concurrent setups with the right token: exactly one wins.
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            factory.CreateClient().PostAsJsonAsync("/api/v1/auth/setup", new { username = $"admin{i}", password = "correct-horse", setupToken = token }, ct)));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(attempts.Where(r => r.StatusCode != HttpStatusCode.NoContent), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));

        // The token is single use and removed from disk.
        Assert.False(File.Exists(tokenFile));
    }
}

public sealed class RateLimitTests(StrictRateLimitFactory factory) : IClassFixture<StrictRateLimitFactory>
{
    [Fact]
    public async Task Credential_endpoints_are_rate_limited_per_client()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = $"user{i}", password = "wrong-password" }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "user9", password = "wrong-password" }, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Other endpoints are not affected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/status", ct)).StatusCode);
    }
}
