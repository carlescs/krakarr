using Krakarr.Core.Jobs;
using Krakarr.Core.MediaServers;
using Krakarr.Core.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Movies;

public static class MoviesServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrMovies(this IServiceCollection services)
    {
        services.AddTmdbApi();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMediaServerNotifier, NullMediaServerNotifier>();
        services.TryAddSingleton<Krakarr.Core.Push.IPushNotifier, Krakarr.Core.Push.NullPushNotifier>();
        services.AddScoped<ITmdbClient, TmdbClient>();
        services.AddScoped<IMovieDiscovery, TmdbMovieDiscovery>();
        services.AddScoped<MovieService>();
        services.AddScoped<Downloads.IManualDownloadLinker, MovieDownloadLinker>();
        services.AddScoped<LibraryImport.MovieLibraryImport>();
        services.AddScheduledTask<MovieImportService>();
        services.AddScheduledTask<MovieRssSyncTask>();
        services.AddScheduledTask<MovieRefreshTask>();
        return services;
    }
}
