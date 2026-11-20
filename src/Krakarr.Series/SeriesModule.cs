using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Series;

public static class SeriesModule
{
    public static IServiceCollection AddKrakarrSeries(this IServiceCollection services)
    {
        services.AddTmdbApi();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITmdbTvClient, TmdbTvClient>();
        services.AddScoped<SeriesService>();
        services.AddScoped<LibraryImport.SeriesLibraryImport>();
        services.AddScheduledTask<SeriesImportService>();
        services.AddScheduledTask<SeriesRssSyncTask>();
        services.AddScheduledTask<SeriesRefreshTask>();
        return services;
    }
}
