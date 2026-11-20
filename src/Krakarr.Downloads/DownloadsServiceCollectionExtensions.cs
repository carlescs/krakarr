using Krakarr.Downloads.QBittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads;

public static class DownloadsServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrDownloads(this IServiceCollection services)
    {
        // Built-in engine (runs only while selected).
        services.AddSingleton<EmbeddedTorrentEngine>();
        services.AddSingleton<IDownloadClientImpl>(sp => sp.GetRequiredService<EmbeddedTorrentEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<EmbeddedTorrentEngine>());

        // External qBittorrent (syncs only while selected).
        services.AddSingleton<QBittorrentApiFactory>();
        services.AddSingleton<QBittorrentEngine>();
        services.AddSingleton<IDownloadClientImpl>(sp => sp.GetRequiredService<QBittorrentEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<QBittorrentEngine>());

        // Everyone else talks to whichever client is selected.
        services.AddSingleton<DownloadClientRouter>();
        services.AddSingleton<ITorrentEngine>(sp => sp.GetRequiredService<DownloadClientRouter>());

        services.AddScoped<IDownloadService, DownloadService>();
        services.AddScoped<ManualDownloadLinks>();
        services.AddHttpClient(DownloadService.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
