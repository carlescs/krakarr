using Krakarr.Host.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Krakarr.Host.Tests;

/// <summary>The URL the Docker HEALTHCHECK's <c>--healthcheck</c> probe calls.</summary>
public sealed class HealthProbeTests
{
    private static Uri Build(params (string Key, string Value)[] settings) =>
        HealthProbe.BuildUri(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void Defaults_to_port_8787_without_url_base() =>
        Assert.Equal("http://127.0.0.1:8787/api/v1/health", Build().ToString());

    [Fact]
    public void Uses_the_first_aspnetcore_http_port() =>
        Assert.Equal("http://127.0.0.1:9000/api/v1/health", Build(("ASPNETCORE_HTTP_PORTS", "9000;9001")).ToString());

    [Theory]
    [InlineData("http://*:7000", 7000)]
    [InlineData("https://+:443;http://+:7001", 7001)]
    [InlineData("http://localhost", 80)]
    public void Uses_the_port_of_the_first_http_url(string urls, int port) =>
        Assert.Equal(port, Build(("ASPNETCORE_URLS", urls), ("ASPNETCORE_HTTP_PORTS", "9000")).Port);

    [Fact]
    public void Falls_back_to_the_krakarr_port() =>
        Assert.Equal(8123, Build(("Krakarr:Port", "8123")).Port);

    [Theory]
    [InlineData("/krakarr")]
    [InlineData("krakarr/")]
    public void Honours_the_url_base(string urlBase) =>
        Assert.Equal("http://127.0.0.1:8787/krakarr/api/v1/health", Build(("Krakarr:UrlBase", urlBase)).ToString());

    [Fact]
    public void Recognises_the_flag()
    {
        Assert.True(HealthProbe.Requested(["--healthcheck"]));
        Assert.False(HealthProbe.Requested(["--urls", "http://*:80"]));
    }
}

public sealed class UrlBaseFactory : KrakarrFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Krakarr:UrlBase", "/krakarr");
    }
}

/// <summary>With a URL base, the API (and so the health probe's URL) answers under it.</summary>
public sealed class UrlBaseTests(UrlBaseFactory factory) : IClassFixture<UrlBaseFactory>
{
    [Fact]
    public async Task Api_answers_under_the_url_base()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/krakarr/api/v1/health", ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
