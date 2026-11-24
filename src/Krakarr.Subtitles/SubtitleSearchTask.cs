using Krakarr.Core.Configuration;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles;

/// <summary>
/// Searches subtitles for newly imported files (never searched) right away and re-checks files that are still
/// missing languages every <see cref="SubtitleSettings.SearchIntervalHours"/>, except files marked "not found"
/// (<see cref="Krakarr.Core.Domain.MovieFile.SubtitlesNotFound"/>, <see cref="Krakarr.Core.Domain.EpisodeFile.SubtitlesNotFound"/>). Processes a few files per run to
/// respect provider quotas.
/// </summary>
public sealed class SubtitleSearchTask(
    KrakarrDbContext db,
    SubtitleService service,
    ISettingsService settings,
    TimeProvider time,
    ILogger<SubtitleSearchTask> logger) : IScheduledTask
{
    private const int BatchSize = 10;

    public string Name => "Search missing subtitles";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public TimeSpan InitialDelay => TimeSpan.FromSeconds(30);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync<SubtitleSettings>(cancellationToken);
        if (!s.Enabled || s.Languages.Count == 0 || !service.Providers.Any(p => p.IsConfigured(s))) return;
        if (service.BackoffUntil(s) is not null) return; // rate limited or out of quota: try again on a later run

        // When the wanted languages change, every file becomes due again. When the configured providers change, files
        // where nothing was found are searched again, since the new providers may have them.
        var signature = string.Join(',', s.Languages.Order(StringComparer.Ordinal));
        var providersSignature = string.Join(',', service.Providers.Where(p => p.IsConfigured(s)).Select(p => p.Name).Order(StringComparer.Ordinal));
        var state = await settings.GetAsync<SubtitleSearchState>(cancellationToken);
        if (state.LanguagesSignature != signature || state.ProvidersSignature != providersSignature)
        {
            if (state.LanguagesSignature is not null && state.LanguagesSignature != signature)
            {
                logger.LogInformation("Subtitle languages changed ({Old} → {New}); re-checking all files", state.LanguagesSignature, signature);
                await db.MovieFiles.ExecuteUpdateAsync(x => x
                    .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null)
                    .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), cancellationToken);
                await db.EpisodeFiles.ExecuteUpdateAsync(x => x
                    .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null)
                    .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), cancellationToken);
            }
            else if (state.ProvidersSignature is not null && state.ProvidersSignature != providersSignature)
            {
                logger.LogInformation("Subtitle providers changed ({Old} → {New}); re-checking files without subtitles found", state.ProvidersSignature, providersSignature);
                await db.MovieFiles.Where(f => f.SubtitlesNotFound != null).ExecuteUpdateAsync(x => x
                    .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null)
                    .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), cancellationToken);
                await db.EpisodeFiles.Where(f => f.SubtitlesNotFound != null).ExecuteUpdateAsync(x => x
                    .SetProperty(f => f.LastSubtitleSearch, (DateTimeOffset?)null)
                    .SetProperty(f => f.SubtitlesNotFound, (DateTimeOffset?)null), cancellationToken);
            }

            await settings.SaveAsync(new SubtitleSearchState { LanguagesSignature = signature, ProvidersSignature = providersSignature }, cancellationToken);
        }

        var due = time.GetUtcNow() - TimeSpan.FromHours(Math.Max(1, s.SearchIntervalHours));
        // DateTimeOffset comparisons are translated on both providers (SQLite stores them as sortable binary).
        // Files marked "not found" (every provider came up empty, or the user marked them) are not retried.
        var movieFiles = await db.MovieFiles.AsNoTracking()
            .Where(f => f.SubtitlesNotFound == null)
            .Where(f => f.LastSubtitleSearch == null || f.LastSubtitleSearch < due)
            .OrderBy(f => f.LastSubtitleSearch != null).ThenBy(f => f.LastSubtitleSearch).ThenBy(f => f.Id)
            .Select(f => f.Id).Take(BatchSize).ToListAsync(cancellationToken);
        var episodeFiles = await db.EpisodeFiles.AsNoTracking()
            .Where(f => f.SubtitlesNotFound == null)
            .Where(f => f.LastSubtitleSearch == null || f.LastSubtitleSearch < due)
            .OrderBy(f => f.LastSubtitleSearch != null).ThenBy(f => f.LastSubtitleSearch).ThenBy(f => f.Id)
            .Select(f => f.Id).Take(BatchSize).ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var id in movieFiles)
        {
            if (processed++ >= BatchSize || service.BackoffUntil(s) is not null) break;
            if (await service.GetMovieTargetAsync(id, cancellationToken) is { } target)
                logger.LogInformation("Subtitles: {Result}", await service.SearchMissingAsync(target, cancellationToken));
        }

        foreach (var id in episodeFiles)
        {
            if (processed++ >= BatchSize || service.BackoffUntil(s) is not null) break;
            if (await service.GetEpisodeTargetAsync(id, cancellationToken) is { } target)
                logger.LogInformation("Subtitles: {Result}", await service.SearchMissingAsync(target, cancellationToken));
        }
    }
}
