using Krakarr.Core.Configuration;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles;

/// <summary>
/// Searches subtitles for newly imported files (never searched) right away and re-checks files that are still
/// missing languages (or holding a recent low-scoring subtitle worth upgrading) every
/// <see cref="SubtitleSettings.SearchIntervalHours"/>, except files marked "not found"
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
    private const int UpgradeBatchSize = 5;

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

        HashSet<int> doneMovies = [], doneEpisodes = [];
        await SearchAsync(s, movieFiles, [], doneMovies, BatchSize, movies: true, cancellationToken);
        await SearchAsync(s, episodeFiles, [], doneEpisodes, BatchSize - doneMovies.Count, movies: false, cancellationToken);
        await UpgradeAsync(s, due, doneMovies, doneEpisodes, cancellationToken);
    }

    /// <summary>Searches up to <paramref name="budget"/> of the files, skipping <paramref name="skip"/> and adding the ones searched to <paramref name="done"/>.</summary>
    private async Task SearchAsync(SubtitleSettings s, IEnumerable<int> ids, HashSet<int> skip, HashSet<int> done, int budget, bool movies, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            if (done.Count >= budget || service.BackoffUntil(s) is not null) return;
            if (skip.Contains(id)) continue;
            done.Add(id);
            var target = movies ? await service.GetMovieTargetAsync(id, ct) : await service.GetEpisodeTargetAsync(id, ct);
            if (target is not null) logger.LogInformation("Subtitles: {Result}", await service.SearchMissingAsync(target, ct));
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Files with a recent subtitle scoring below <see cref="SubtitleSettings.UpgradeBelowScore"/> that are due again
    /// get their own small budget, so a long backlog of files to re-check doesn't push upgrades past their window.
    /// The search itself decides which languages to upgrade (<see cref="SubtitleUpgrade.Select"/>).
    /// </summary>
    private async Task UpgradeAsync(SubtitleSettings s, DateTimeOffset due, HashSet<int> doneMovies, HashSet<int> doneEpisodes, CancellationToken ct)
    {
        if (s.UpgradeDays <= 0) return;
        var since = time.GetUtcNow() - TimeSpan.FromDays(s.UpgradeDays);
        var candidates = db.SubtitleFiles.AsNoTracking()
            .Where(x => !x.Forced && !x.Manual && x.Score != null && x.Score < s.UpgradeBelowScore && x.DateAdded >= since
                        && x.Provider != SubtitleUpgrade.DiskProvider);
        var movieFiles = await candidates.Where(x => x.MovieFileId != null)
            .Join(db.MovieFiles.AsNoTracking(), x => x.MovieFileId, f => f.Id, (x, f) => f)
            .Where(f => f.LastSubtitleSearch == null || f.LastSubtitleSearch < due)
            .Select(f => f.Id).Distinct().OrderBy(id => id).Take(UpgradeBatchSize).ToListAsync(ct);
        var episodeFiles = await candidates.Where(x => x.EpisodeFileId != null)
            .Join(db.EpisodeFiles.AsNoTracking(), x => x.EpisodeFileId, f => f.Id, (x, f) => f)
            .Where(f => f.LastSubtitleSearch == null || f.LastSubtitleSearch < due)
            .Select(f => f.Id).Distinct().OrderBy(id => id).Take(UpgradeBatchSize).ToListAsync(ct);

        HashSet<int> upgradedMovies = [], upgradedEpisodes = [];
        await SearchAsync(s, movieFiles, doneMovies, upgradedMovies, UpgradeBatchSize, movies: true, ct);
        await SearchAsync(s, episodeFiles, doneEpisodes, upgradedEpisodes, UpgradeBatchSize - upgradedMovies.Count, movies: false, ct);
    }
}
