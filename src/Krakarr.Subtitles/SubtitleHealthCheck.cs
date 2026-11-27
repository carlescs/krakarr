using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Health;
using Krakarr.Data;

namespace Krakarr.Subtitles;

/// <summary>
/// Subtitles are on but no provider has an API key, or a provider rejects its key. Provider tests go through
/// <see cref="HealthValidationCache"/>, so they run when the provider settings change, daily after a success and
/// hourly after a failure, and never while a provider asked us to back off.
/// </summary>
public sealed class SubtitleHealthCheck(ISettingsService settings, IEnumerable<ISubtitleProvider> providers, HealthValidationCache cache, TimeProvider time)
    : IHealthCheck
{
    public string Id => "subtitles";
    public string Name => "Subtitles";

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var s = await settings.GetAsync<SubtitleSettings>(ct);
        if (!s.Enabled) return [];

        var configured = providers.Where(p => p.IsConfigured(s)).ToList();
        if (configured.Count == 0)
        {
            return [new HealthProblem("none", HealthSeverity.Error,
                "Subtitles are on, but no subtitle provider is enabled with an API key (e.g. an OpenSubtitles.com key), so none can be downloaded.",
                "/settings/subtitles")];
        }

        var problems = new List<HealthProblem>();
        var fingerprint = JsonSerializer.Serialize(new { s.OpenSubtitles, s.Subdl });
        foreach (var provider in configured)
        {
            if (provider.BackoffUntil > time.GetUtcNow()) continue; // the provider is reachable, just rate limiting us

            var problem = await cache.GetAsync($"subtitles.{provider.Name}", fingerprint, async token =>
            {
                try
                {
                    await provider.TestAsync(s, token);
                    return null;
                }
                catch (Exception ex) when (ex is SubtitleProviderException or HttpRequestException || (ex is OperationCanceledException && !token.IsCancellationRequested))
                {
                    return new HealthProblem(provider.Name, HealthSeverity.Warning, $"{provider.DisplayName} doesn't work: {ex.Message}", "/settings/subtitles");
                }
            }, ct);
            if (problem is not null) problems.Add(problem);
        }

        return problems;
    }
}
