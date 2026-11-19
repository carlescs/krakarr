using Krakarr.Core.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Series;

public static class SeriesModule
{
    public static IServiceCollection AddKrakarrSeries(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(TvdbClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri((configuration["Tvdb:BaseUrl"] ?? "https://api4.thetvdb.com/v4/").TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITvdbClient, TvdbClient>();
        services.AddScoped<SeriesService>();
        services.AddScheduledTask<SeriesImportService>();
        services.AddScheduledTask<SeriesRssSyncTask>();
        services.AddScheduledTask<SeriesRefreshTask>();
        return services;
    }
}
