using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Indexers;

public static class IndexersServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrIndexers(this IServiceCollection services)
    {
        services.AddHttpClient(TorznabClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Krakarr/0.1");
        });
        services.AddScoped<ITorznabClient, TorznabClient>();
        services.AddScoped<IProwlarrClient, ProwlarrClient>();
        services.AddScoped<IIndexerSearchService, IndexerSearchService>();
        return services;
    }
}
