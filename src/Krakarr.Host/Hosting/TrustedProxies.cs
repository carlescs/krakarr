using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Krakarr.Host.Hosting;

/// <summary>
/// Which reverse proxies may set <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c> (setting <c>Krakarr:TrustedProxies</c>).
/// Forwarded headers from any other address are ignored, so clients cannot spoof their IP (used for rate limiting and
/// logs) or the request scheme (used for the cookie's Secure flag).
/// </summary>
public static class TrustedProxies
{
    public const string SettingName = "Krakarr:TrustedProxies";

    /// <summary>Parses a list of IP addresses and CIDR ranges separated by commas, semicolons or whitespace.</summary>
    /// <exception cref="FormatException">An entry is neither an IP address nor a CIDR range.</exception>
    public static (List<IPAddress> Proxies, List<System.Net.IPNetwork> Networks) Parse(string? value)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        foreach (var entry in (value ?? string.Empty).Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Contains('/'))
            {
                if (!System.Net.IPNetwork.TryParse(entry, out var network))
                    throw new FormatException($"{SettingName}: '{entry}' is not a valid CIDR range (e.g. 172.16.0.0/12)");
                networks.Add(network);
            }
            else if (IPAddress.TryParse(entry, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                throw new FormatException($"{SettingName}: '{entry}' is not a valid IP address or CIDR range");
            }
        }

        return (proxies, networks);
    }

    /// <summary>
    /// Trusts loopback (the ASP.NET Core default, e.g. a proxy on the same host) plus the configured entries, and only
    /// one proxy hop.
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options, string? setting)
    {
        var (proxies, networks) = Parse(setting);
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
        foreach (var network in networks) options.KnownIPNetworks.Add(network);
    }

    /// <summary>True when <paramref name="remote"/> is allowed to forward headers under <paramref name="options"/>.</summary>
    public static bool IsTrusted(ForwardedHeadersOptions options, IPAddress? remote)
    {
        if (remote is null) return false;
        var candidates = remote.IsIPv4MappedToIPv6 ? new[] { remote, remote.MapToIPv4() } : [remote];
        return candidates.Any(ip => options.KnownProxies.Contains(ip) || options.KnownIPNetworks.Any(n => n.Contains(ip)));
    }

    /// <summary>
    /// Logs a one-time warning when forwarded headers arrive from an untrusted address, which usually means a reverse
    /// proxy that still needs to be listed in <c>Krakarr:TrustedProxies</c>. Must run before <c>UseForwardedHeaders</c>.
    /// </summary>
    public static IApplicationBuilder UseUntrustedProxyWarning(this IApplicationBuilder app)
    {
        var warned = 0;
        return app.Use((context, next) =>
        {
            if (Volatile.Read(ref warned) == 0
                && (context.Request.Headers.ContainsKey("X-Forwarded-For") || context.Request.Headers.ContainsKey("X-Forwarded-Proto")))
            {
                var options = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>().Value;
                var remote = context.Connection.RemoteIpAddress;
                if (!IsTrusted(options, remote) && Interlocked.Exchange(ref warned, 1) == 0)
                {
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Krakarr.Host.TrustedProxies").LogWarning(
                        "Ignoring X-Forwarded-* headers from untrusted address {Remote}. If this is your reverse proxy, add it to Krakarr__TrustedProxies (e.g. {Remote} or its network in CIDR form)",
                        remote, remote);
                }
            }

            return next(context);
        });
    }
}
