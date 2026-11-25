using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Health;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Series;

/// <summary>
/// TMDB key (movies, Discover, and series when TMDB is the series source) and TheTVDB key (when it is) missing or
/// rejected. Validations go through <see cref="HealthValidationCache"/>, so the services are only asked when a key
/// changes, daily after a success and hourly after a failure.
/// </summary>
public sealed class MetadataHealthCheck(ISettingsService settings, TmdbApi tmdb, ITvdbClient tvdb, HealthValidationCache cache) : IHealthCheck
{
    public string Id => "metadata";
    public string Name => "Metadata";

    public async Task<IReadOnlyList<HealthProblem>> CheckAsync(CancellationToken ct = default)
    {
        var meta = await settings.GetAsync<MetadataSettings>(ct);
        var problems = new List<HealthProblem>();

        if (string.IsNullOrWhiteSpace(meta.TmdbApiKey))
        {
            problems.Add(new HealthProblem("tmdb", HealthSeverity.Error,
                "No TMDB API key is set, so movies can't be added or refreshed and Discover stays empty.", "/settings/metadata"));
        }
        else if (await cache.GetAsync("tmdb", meta.TmdbApiKey.Trim(), token => ValidateTmdbAsync(meta, token), ct) is { } tmdbProblem)
        {
            problems.Add(tmdbProblem);
        }

        if (meta.SeriesSource == SeriesMetadataSource.Tvdb)
        {
            if (string.IsNullOrWhiteSpace(meta.TvdbApiKey))
            {
                problems.Add(new HealthProblem("tvdb", HealthSeverity.Error,
                    "TheTVDB is the series metadata source but no TheTVDB API key is set, so series can't be added or refreshed.", "/settings/metadata"));
            }
            else if (await cache.GetAsync("tvdb", $"{meta.TvdbApiKey.Trim()}\n{meta.TvdbPin}", ValidateTvdbAsync, ct) is { } tvdbProblem)
            {
                problems.Add(tvdbProblem);
            }
        }

        return problems;
    }

    private async Task<HealthProblem?> ValidateTmdbAsync(MetadataSettings meta, CancellationToken ct)
    {
        try
        {
            await tmdb.GetAsync<JsonElement>(meta, "3/configuration", ct);
            return null;
        }
        catch (MetadataException ex) when (ex.IsAuthenticationFailure)
        {
            return new HealthProblem("tmdb", HealthSeverity.Error, "TMDB rejected the API key. Check it in Settings → Metadata.", "/settings/metadata");
        }
        catch (Exception ex) when (ex is MetadataException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new HealthProblem("tmdb", HealthSeverity.Warning, $"Could not reach TMDB to check the API key: {ex.Message}", "/settings/metadata");
        }
    }

    private async Task<HealthProblem?> ValidateTvdbAsync(CancellationToken ct)
    {
        try
        {
            await tvdb.LoginAsync(ct);
            return null;
        }
        catch (MetadataException ex) when (ex.IsAuthenticationFailure)
        {
            return new HealthProblem("tvdb", HealthSeverity.Error, "TheTVDB rejected the API key or PIN. Check them in Settings → Metadata.", "/settings/metadata");
        }
        catch (Exception ex) when (ex is MetadataException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new HealthProblem("tvdb", HealthSeverity.Warning, $"Could not reach TheTVDB to check the API key: {ex.Message}", "/settings/metadata");
        }
    }
}
