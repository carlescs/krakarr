using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Krakarr.Host.Tests;

/// <summary>Boots the real host against a throw-away config directory (SQLite), without background services.</summary>
public class KrakarrFactory : WebApplicationFactory<Program>
{
    public const string TestSetupToken = "test-setup-token";

    private readonly string _configDir = Directory.CreateTempSubdirectory("krakarr-it").FullName;

    public string ConfigDirectory => _configDir;

    /// <summary>Preset setup token (Krakarr:SetupToken); null lets the host generate one into the config folder.</summary>
    protected virtual string? SetupToken => TestSetupToken;

    /// <summary>Per-IP limit for the credential endpoints; high by default so the shared fixture never trips it.</summary>
    protected virtual int AuthAttemptsPerMinutePerIp => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Krakarr:ConfigDirectory", _configDir);
        if (SetupToken is { } token) builder.UseSetting("Krakarr:SetupToken", token);
        builder.UseSetting("Krakarr:Auth:AttemptsPerMinutePerIp", AuthAttemptsPerMinutePerIp.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
