using Krakarr.Core.Health;
using Krakarr.Core.Jobs;
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
        services.AddHttpClient(TvdbClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri((configuration["Tvdb:BaseUrl"] ?? "https://api4.thetvdb.com/v4/").TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
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
