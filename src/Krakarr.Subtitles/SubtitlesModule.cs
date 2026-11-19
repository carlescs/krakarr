using Krakarr.Core.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Krakarr.Subtitles;

public static class SubtitlesModule
{
    public static IServiceCollection AddKrakarrSubtitles(this IServiceCollection services)
    {
        services.AddHttpClient(OpenSubtitlesProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISubtitleProvider, OpenSubtitlesProvider>();
        services.AddScoped<SubtitleService>();
        services.AddScheduledTask<SubtitleSearchTask>();
        return services;
    }
}
