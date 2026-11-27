using Krakarr.Core.Configuration;
using Krakarr.Core.Jobs;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Subtitles.Sync;

/// <summary>
/// With <see cref="SubtitleSyncSettings.Automatic"/>, syncs subtitles downloaded in the last two days that score below
/// <see cref="SubtitleSyncSettings.BelowScore"/>, one at a time. Each subtitle is tried once: the outcome is kept in
/// <see cref="Krakarr.Core.Domain.SubtitleFile.SyncNote"/>, and older subtitles are only synced by hand.
/// </summary>
public sealed class SubtitleSyncTask(
    KrakarrDbContext db,
    SubtitleSyncService sync,
    ISettingsService settings,
    TimeProvider time,
    ILogger<SubtitleSyncTask> logger) : IScheduledTask
{
    private const int BatchSize = 3;
    private static readonly TimeSpan Window = TimeSpan.FromDays(2);

    public string Name => "Sync subtitles";
    public TimeSpan Interval => TimeSpan.FromMinutes(2);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(1);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var s = await settings.GetAsync<SubtitleSettings>(cancellationToken);
        if (!s.Sync.Automatic) return;
        if (!SubtitleSyncTools.Find(s.Sync).Any) return; // nothing can sync on its own: leave them for when a tool is installed

        var since = time.GetUtcNow() - Window;
        var ids = await db.SubtitleFiles.AsNoTracking()
            .Where(x => x.SyncedAt == null && x.SyncNote == null && x.Score != null && x.Score < s.Sync.BelowScore
                        && x.DateAdded >= since && x.Provider != SubtitleUpgrade.DiskProvider)
            .OrderBy(x => x.DateAdded).Select(x => x.Id).Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var id in ids)
        {
            if (await sync.SyncAsync(id, cancellationToken) is { } result)
                logger.LogDebug("Automatic subtitle sync {Id}: {Result}", id, result.Message);
            db.ChangeTracker.Clear();
        }
    }
}
