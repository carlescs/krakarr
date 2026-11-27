using Krakarr.Core.Health;
using Krakarr.Core.Jobs;
using Krakarr.Core.MediaServers;
using Krakarr.Core.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Series;

public static class SeriesModule
{
    public static IServiceCollection AddKrakarrSeries(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTmdbApi();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMediaServerNotifier, NullMediaServerNotifier>();
        services.TryAddSingleton<Krakarr.Core.Push.IPushNotifier, Krakarr.Core.Push.NullPushNotifier>();
        services.AddHttpClient(TvdbClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri((configuration["Tvdb:BaseUrl"] ?? "https://api4.thetvdb.com/v4/").TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddHttpClient(XemClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri((configuration["Xem:BaseUrl"] ?? "https://thexem.info/").TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(20);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddScoped<IXemClient, XemClient>();
        services.AddScoped<ITmdbTvClient, TmdbTvClient>();
        services.AddScoped<ITvdbClient, TvdbClient>();
        services.AddScoped<ISeriesMetadata, SeriesMetadata>();
        services.AddScoped<IHealthCheck, MetadataHealthCheck>();
        services.AddScoped<ISeriesDiscovery, TmdbSeriesDiscovery>();
        services.AddScoped<SeriesService>();
        services.AddScoped<Downloads.IManualDownloadLinker, SeriesDownloadLinker>();
        services.AddScoped<SeriesOrganizer>();
        services.AddScoped<LibraryImport.SeriesLibraryImport>();
        services.AddScheduledTask<SeriesImportService>();
        services.AddScheduledTask<SeriesRssSyncTask>();
        services.AddScheduledTask<SeriesRefreshTask>();
        return services;
    }
}
