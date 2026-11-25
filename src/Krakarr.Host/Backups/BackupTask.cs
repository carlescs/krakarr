using Krakarr.Core.Configuration;
using Krakarr.Core.Jobs;
using Krakarr.Data;

namespace Krakarr.Host.Backups;

/// <summary>Scheduled backup (weekly by default, see <see cref="BackupSettings"/>).</summary>
public sealed class BackupTask(BackupService backups, ISettingsService settings, TimeProvider time) : IScheduledTask
{
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMinutes(10);

    public string Name => "Backup";

    public TimeSpan Interval => TimeSpan.FromDays(7);

    /// <summary>
    /// The schedule lives in memory, so count the first run from the newest scheduled backup on disk; otherwise every
    /// restart would make a new backup. Never sooner than a few minutes after startup.
    /// </summary>
    public TimeSpan InitialDelay
    {
        get
        {
            var interval = settings.GetAsync<BackupSettings>().GetAwaiter().GetResult().Interval ?? Interval;
            if (backups.LastScheduled() is not { } last) return MinimumDelay;
            var due = last + interval - time.GetUtcNow();
            return due < MinimumDelay ? MinimumDelay : due > interval ? interval : due;
        }
    }

    public async ValueTask<TimeSpan?> GetIntervalAsync(CancellationToken cancellationToken) =>
        (await settings.GetAsync<BackupSettings>(cancellationToken)).Interval;

    public Task ExecuteAsync(CancellationToken cancellationToken) => backups.CreateAsync(BackupType.Scheduled, wait: true, cancellationToken);
}
