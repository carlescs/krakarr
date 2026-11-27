using Krakarr.Host.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace Krakarr.Host.Tests;

public sealed class HstsFactory : KrakarrFactory
{
    protected override string? TrustedProxies => "10.0.0.0/8";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Krakarr:Hsts", "true");
        builder.UseSetting("Krakarr:ContentSecurityPolicy", "default-src 'self'; img-src 'self' https://images.example");
    }
}

public sealed class SecurityHeadersTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    [Theory]
    [InlineData("/api/v1/auth/status")] // API
    [InlineData("/movies")] // SPA
    [InlineData("/api/v1/movies")] // 401
    [InlineData("/api/v1/nope")] // 404
    public async Task Every_response_carries_the_hardening_headers(string path)
    {
        var res = await factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(SecurityHeaders.DefaultContentSecurityPolicy, res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        var permissions = res.Headers.GetValues("Permissions-Policy").Single();
        Assert.Contains("camera=()", permissions);
        // Passkey sign-in needs WebAuthn in Krakarr's own pages.
        Assert.Contains("publickey-credentials-get=(self)", permissions);
        Assert.Contains("publickey-credentials-create=(self)", permissions);
        Assert.False(res.Headers.Contains("Strict-Transport-Security")); // opt-in
    }
}

public sealed class HstsTests(HstsFactory factory) : IClassFixture<HstsFactory>
{
    [Fact]
    public async Task Hsts_is_only_sent_over_https_when_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();

        var plain = await client.GetAsync("/api/v1/auth/status", ct);
        Assert.False(plain.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("default-src 'self'; img-src 'self' https://images.example", plain.Headers.GetValues("Content-Security-Policy").Single());

        // HTTPS terminated by a trusted reverse proxy.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/status");
        request.Headers.Add(KrakarrFactory.RemoteIpHeader, "10.1.2.3");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var https = await client.SendAsync(request, ct);
        Assert.Equal(SecurityHeaders.StrictTransportSecurity, https.Headers.GetValues("Strict-Transport-Security").Single());
    }
}
