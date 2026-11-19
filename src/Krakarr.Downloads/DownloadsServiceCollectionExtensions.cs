using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads;

public static class DownloadsServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrDownloads(this IServiceCollection services)
    {
        services.AddSingleton<TorrentEngine>();
        services.AddSingleton<ITorrentEngine>(sp => sp.GetRequiredService<TorrentEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<TorrentEngine>());
        services.AddScoped<IDownloadService, DownloadService>();
        services.AddHttpClient(DownloadService.HttpClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(60);
                c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
