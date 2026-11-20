using Krakarr.Core.Jobs;
using Krakarr.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrMovies(this IServiceCollection services)
    {
        services.AddTmdbApi();
        services.AddScoped<ITmdbClient, TmdbClient>();
        services.AddScoped<MovieService>();
        services.AddScoped<LibraryImport.MovieLibraryImport>();
        services.AddScheduledTask<MovieImportService>();
        services.AddScheduledTask<MovieRssSyncTask>();
        services.AddScheduledTask<MovieRefreshTask>();
        return services;
    }
}
