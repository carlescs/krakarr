using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Krakarr.Host.Tests;

/// <summary>Boots the real host against a throw-away config directory (SQLite), without background services.</summary>
public sealed class KrakarrFactory : WebApplicationFactory<Program>
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("krakarr-it").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Krakarr:ConfigDirectory", _configDir);
        builder.UseSetting("Krakarr:DownloadDirectory", Path.Combine(_configDir, "downloads"));
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // The torrent engine and schedulers bind ports / run timers; not needed for API tests.
            foreach (var d in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
            {
                services.Remove(d);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
