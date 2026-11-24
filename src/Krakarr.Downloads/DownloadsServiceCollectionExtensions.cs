using Krakarr.Downloads.QBittorrent;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads;

public static class DownloadsServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrDownloads(this IServiceCollection services)
    {
        services.AddSingleton<DownloadStatusFeed>();

        // Built-in engine (runs while selected or while it owns downloads).
        services.AddSingleton<EmbeddedTorrentEngine>();
        services.AddSingleton<IDownloadClientImpl>(sp => sp.GetRequiredService<EmbeddedTorrentEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<EmbeddedTorrentEngine>());

        // External qBittorrent (syncs while selected or while it owns downloads).
        services.AddSingleton<QBittorrentApiFactory>();
        services.AddSingleton<QBittorrentEngine>();
        services.AddSingleton<IDownloadClientImpl>(sp => sp.GetRequiredService<QBittorrentEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<QBittorrentEngine>());

        // Everyone else goes through the router: new torrents to the selected client, the rest to the owning client.
        services.AddSingleton<DownloadClientRouter>();
        services.AddSingleton<ITorrentEngine>(sp => sp.GetRequiredService<DownloadClientRouter>());

        services.AddScoped<IDownloadService, DownloadService>();
        services.AddScoped<ManualDownloadLinks>();
        services.AddScoped<ImportIssues>();
        services.AddHttpClient(DownloadService.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
