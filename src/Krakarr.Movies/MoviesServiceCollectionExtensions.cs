using Krakarr.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrMovies(this IServiceCollection services)
    {
        services.AddHttpClient(TmdbClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddScoped<ITmdbClient, TmdbClient>();
        services.AddScoped<MovieService>();
        services.AddScheduledTask<MovieImportService>();
        services.AddScheduledTask<MovieRssSyncTask>();
        services.AddScheduledTask<MovieRefreshTask>();
        return services;
    }
}
