using System.Net;
using System.Net.Http.Json;
using Krakarr.Host.Hosting;
using Microsoft.AspNetCore.Builder;

namespace Krakarr.Host.Tests;

public sealed class TrustedProxiesParsingTests
{
    [Fact]
    public void Parses_addresses_and_networks_with_any_separator()
    {
        var (proxies, networks) = TrustedProxies.Parse("192.168.1.10, 172.16.0.0/12;fd00::/8  ::1");
        Assert.Equal([IPAddress.Parse("192.168.1.10"), IPAddress.IPv6Loopback], proxies);
        Assert.Equal([System.Net.IPNetwork.Parse("172.16.0.0/12"), System.Net.IPNetwork.Parse("fd00::/8")], networks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_setting_adds_nothing(string? value)
    {
        var (proxies, networks) = TrustedProxies.Parse(value);
        Assert.Empty(proxies);
        Assert.Empty(networks);
    }

    [Theory]
    [InlineData("proxy.local")]
    [InlineData("10.0.0.0/33")]
    [InlineData("300.1.1.1")]
    public void Invalid_entries_fail_loudly(string value)
    {
        Assert.Throws<FormatException>(() => TrustedProxies.Parse(value));
    }

    [Fact]
    public void Defaults_trust_only_loopback_and_configured_entries()
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxies.Configure(options, "10.0.0.0/8");
        Assert.True(TrustedProxies.IsTrusted(options, IPAddress.Loopback));
        Assert.True(TrustedProxies.IsTrusted(options, IPAddress.Parse("10.1.2.3")));
        Assert.True(TrustedProxies.IsTrusted(options, IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.False(TrustedProxies.IsTrusted(options, IPAddress.Parse("203.0.113.5")));
        Assert.False(TrustedProxies.IsTrusted(options, null));
        Assert.Equal(1, options.ForwardLimit);
    }
}

/// <summary>Host that trusts 10.0.0.0/8 as reverse proxies, with a small per-IP credential limit.</summary>
public sealed class TrustedProxyFactory : KrakarrFactory
{
    protected override string? TrustedProxies => "10.0.0.0/8";
    protected override int AuthAttemptsPerMinutePerIp => 3;
}

public sealed class SecureCookieFactory : KrakarrFactory
{
    protected override bool SecureCookies => true;
}

public sealed class ForwardedHeadersTests(TrustedProxyFactory factory) : IClassFixture<TrustedProxyFactory>
{
    private static HttpRequestMessage Login(string remoteIp, string? forwardedFor = null, string? forwardedProto = null, string password = "wrong-password")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = $"user-{Guid.NewGuid():N}", password }),
        };
        request.Headers.Add(KrakarrFactory.RemoteIpHeader, remoteIp);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (forwardedProto is not null) request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        return request;
    }

    [Fact]
    public async Task Untrusted_clients_cannot_dodge_the_rate_limit_by_spoofing_X_Forwarded_For()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 1; i <= 4; i++)
        {
            codes.Add((await client.SendAsync(Login("203.0.113.5", forwardedFor: $"198.51.100.{i}"), ct)).StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], codes);
    }

    [Fact]
    public async Task Trusted_proxy_forwards_the_real_client_ip()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();

        // Different clients behind the same proxy get their own rate-limit buckets...
        for (var i = 1; i <= 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Login("10.0.0.2", forwardedFor: $"192.0.2.{i}"), ct)).StatusCode);
        }

        // ...while one client behind it is still limited.
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            codes.Add((await client.SendAsync(Login("10.0.0.2", forwardedFor: "192.0.2.200"), ct)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Login("10.0.0.2", forwardedFor: "192.0.2.200"), ct)).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes);
    }

    [Fact]
    public async Task Only_a_trusted_proxy_can_mark_the_request_as_https()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();

        var setup = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/setup")
        {
            Content = JsonContent.Create(new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }),
        };
        setup.Headers.Add(KrakarrFactory.RemoteIpHeader, "10.0.0.3");
        setup.Headers.Add("X-Forwarded-For", "192.0.2.50");
        setup.Headers.Add("X-Forwarded-Proto", "https");
        var viaProxy = await client.SendAsync(setup, ct);
        Assert.Equal(HttpStatusCode.NoContent, viaProxy.StatusCode);
        Assert.Contains("secure", AuthCookie(viaProxy), StringComparison.OrdinalIgnoreCase);

        var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { username = "admin", password = "correct-horse" }),
        };
        login.Headers.Add(KrakarrFactory.RemoteIpHeader, "203.0.113.77");
        login.Headers.Add("X-Forwarded-Proto", "https");
        var spoofed = await client.SendAsync(login, ct);
        Assert.Equal(HttpStatusCode.NoContent, spoofed.StatusCode);
        Assert.DoesNotContain("secure", AuthCookie(spoofed), StringComparison.OrdinalIgnoreCase);
    }

    internal static string AuthCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("krakarr_auth=", StringComparison.Ordinal));
}

public sealed class SecureCookieTests(SecureCookieFactory factory) : IClassFixture<SecureCookieFactory>
{
    [Fact]
    public async Task SecureCookies_setting_marks_the_cookie_secure_over_plain_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/setup",
            new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains("secure", ForwardedHeadersTests.AuthCookie(res), StringComparison.OrdinalIgnoreCase);
    }
}
