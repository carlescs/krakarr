using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddKrakarrData(this IServiceCollection services)
    {
        services.AddSingleton<SettingsCache>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<DatabaseSeeder>();
        return services;
    }
}
