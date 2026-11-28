using Microsoft.Extensions.Configuration;

namespace Krakarr.Host.Hosting;

/// <summary>
/// <c>Krakarr.Host --healthcheck</c>: asks the running instance for <c>/api/v1/health</c> on loopback and exits 0 when it
/// answers with a success status, 1 otherwise. The Docker <c>HEALTHCHECK</c> uses it, so the runtime image needs no curl.
/// It runs before the web host is built and reads only the settings that decide the URL (port and URL base).
/// </summary>
public static class HealthProbe
{
    public const string Flag = "--healthcheck";

    private const int DefaultPort = 8787;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    public static bool Requested(string[] args) => args.Contains(Flag, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync()
    {
        try
        {
            var uri = BuildUri(LoadConfiguration());
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            // The JSON check tells the API apart from the SPA's HTML fallback page, which also answers 200.
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json") return 0;
            await Console.Error.WriteLineAsync($"{uri} answered {(int)response.StatusCode} {response.Content.Headers.ContentType}");
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync(e.Message);
        }
        return 1;
    }

    /// <summary>The same sources <c>Program</c> reads the port and URL base from: config.json, then the environment.</summary>
    private static IConfiguration LoadConfiguration()
    {
        var env = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var inContainer = env["DOTNET_RUNNING_IN_CONTAINER"] == "true";
        var configDir = env["Krakarr:ConfigDirectory"]
            ?? (inContainer ? "/config" : Path.Combine(Directory.GetCurrentDirectory(), "data"));
        try
        {
            return new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(Path.GetFullPath(configDir), "config.json"), optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            // The probe may run as a user that can't read /config (e.g. root without DAC_OVERRIDE): use the environment.
            return env;
        }
    }

    /// <summary>
    /// The loopback health URL: the first port of <c>ASPNETCORE_URLS</c> or <c>ASPNETCORE_HTTP_PORTS</c> (what the host
    /// listens on when set), else <c>Krakarr:Port</c>, else 8787, under <c>Krakarr:UrlBase</c>.
    /// </summary>
    public static Uri BuildUri(IConfiguration config)
    {
        var port = PortFromUrls(config["ASPNETCORE_URLS"] ?? config["urls"])
            ?? FirstPort(config["ASPNETCORE_HTTP_PORTS"] ?? config["http_ports"])
            ?? FirstPort(config["Krakarr:Port"])
            ?? DefaultPort;
        var urlBase = (config["Krakarr:UrlBase"] ?? string.Empty).Trim('/');
        var path = urlBase.Length == 0 ? "/api/v1/health" : $"/{urlBase}/api/v1/health";
        return new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", port, path).Uri;
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? FirstPort(string? value) =>
        Split(value).Select(p => int.TryParse(p, out var n) && n is > 0 and <= 65535 ? n : (int?)null).FirstOrDefault(p => p is not null);

    /// <summary>The port of the first plain http URL (wildcard hosts such as <c>http://*:8787</c> or <c>http://+:8787</c> included).</summary>
    private static int? PortFromUrls(string? value)
    {
        foreach (var url in Split(value))
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) continue;
            var host = url["http://".Length..].Split('/')[0];
            var colon = host.LastIndexOf(':');
            if (colon < 0 || host.EndsWith(']')) return 80;
            if (int.TryParse(host[(colon + 1)..], out var port) && port is > 0 and <= 65535) return port;
        }
        return null;
    }
}
