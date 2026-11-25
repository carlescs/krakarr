using Krakarr.Core.Health;
using Krakarr.Core.Jobs;
using Krakarr.Subtitles.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace Krakarr.Subtitles;

public static class SubtitlesModule
{
    public static IServiceCollection AddKrakarrSubtitles(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OpenSubtitlesBackoff>();
        services.AddHttpClient(OpenSubtitlesProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddResilienceHandler("opensubtitles-backoff", (pipeline, context) => pipeline.AddOpenSubtitlesBackoff(
                context.ServiceProvider.GetRequiredService<OpenSubtitlesBackoff>(), context.ServiceProvider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient(SubdlProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ISubtitleProvider, OpenSubtitlesProvider>();
        services.AddScoped<ISubtitleProvider, SubdlProvider>();
        services.AddScoped<SubtitleService>();
        services.AddScoped<IHealthCheck, SubtitleHealthCheck>();
        services.AddScheduledTask<SubtitleSearchTask>();
        services.AddSingleton<SubtitleSyncGate>();
        services.AddSingleton<SubtitleSyncProgress>();
        services.AddScoped<Krakarr.Core.Notifications.INotificationSource, SubtitleSyncNotificationSource>();
        services.AddScoped<SubtitleSyncService>();
        services.AddScheduledTask<SubtitleSyncTask>();
        return services;
    }
}
