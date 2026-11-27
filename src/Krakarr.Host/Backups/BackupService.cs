using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Krakarr.Core.Configuration;
using Krakarr.Data;
using Krakarr.Host.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Krakarr.Host.Backups;

public enum BackupType
{
    Scheduled,
    Manual,
}

public sealed record BackupInfo(string Name, BackupType Type, long Size, DateTimeOffset Created);

public sealed class BackupInProgressException() : InvalidOperationException("A backup is already running");

/// <summary>
/// Creates backups of the database, data-protection keys and config.json as zips in <see cref="KrakarrPaths.BackupsDirectory"/>,
/// prunes old ones and stages a backup to be restored on the next start. Archives are written to and read from disk
/// as streams, never built in memory.
/// </summary>
public sealed partial class BackupService(
    KrakarrDbContext db,
    KrakarrPaths paths,
    ISettingsService settings,
    IOptions<DatabaseOptions> dbOptions,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    /// <summary>Largest backup accepted for upload.</summary>
    public const long MaxUploadBytes = 20L * 1024 * 1024 * 1024;

    private const string Prefix = "krakarr_backup_";

    /// <summary>One backup at a time, across scopes (a scheduled run and "Back up now").</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string Provider => dbOptions.Value.ProviderKind;

    public bool RestorePending => File.Exists(paths.PendingRestoreFile);

    [GeneratedRegex(@"^krakarr_backup_(\d{8}_\d{6})_(scheduled|manual)(_\d+)?\.zip$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Backups in the folder, newest first.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(paths.BackupsDirectory)) return [];
        var result = new List<BackupInfo>();
        foreach (var file in new DirectoryInfo(paths.BackupsDirectory).EnumerateFiles(Prefix + "*.zip"))
        {
            var match = NamePattern().Match(file.Name);
            if (!match.Success) continue;
            var created = DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
                ? new DateTimeOffset(stamp, TimeSpan.Zero)
                : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            var type = match.Groups[2].Value == "manual" ? BackupType.Manual : BackupType.Scheduled;
            result.Add(new BackupInfo(file.Name, type, file.Length, created));
        }

        // Backups made in the same second get a _2, _3… suffix, which orders them.
        return result.OrderByDescending(b => b.Created).ThenByDescending(b => Sequence(b.Name)).ToList();
    }

    private static int Sequence(string name) =>
        NamePattern().Match(name).Groups[3].Value is { Length: > 1 } suffix ? int.Parse(suffix.AsSpan(1), CultureInfo.InvariantCulture) : 1;

    /// <summary>Full path of a backup by its file name, or null when the name isn't one of ours or doesn't exist.</summary>
    public string? PathOf(string name)
    {
        if (!NamePattern().IsMatch(name)) return null;
        var path = Path.Combine(paths.BackupsDirectory, name);
        return File.Exists(path) ? path : null;
    }

    public bool Delete(string name)
    {
        if (PathOf(name) is not { } path) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Creates a backup, then deletes the oldest ones beyond the retention count.</summary>
    /// <param name="wait">False to fail with <see cref="BackupInProgressException"/> instead of waiting for a running backup.</param>
    public async Task<BackupInfo> CreateAsync(BackupType type, bool wait = true, CancellationToken ct = default)
    {
        if (wait) await Gate.WaitAsync(ct);
        else if (!await Gate.WaitAsync(0, ct)) throw new BackupInProgressException();
        try
        {
            Directory.CreateDirectory(paths.BackupsDirectory);
            var now = time.GetUtcNow();
            var name = UniqueName(now, type);
            var target = Path.Combine(paths.BackupsDirectory, name);
            var partial = Path.Combine(paths.BackupsDirectory, "." + name + ".partial");
            try
            {
                await WriteArchiveAsync(partial, type, now, ct);
                File.Move(partial, target);
            }
            finally
            {
                File.Delete(partial);
            }

            var info = new BackupInfo(name, type, new FileInfo(target).Length, now);
            logger.LogInformation("Created backup {Name} ({Size} bytes)", name, info.Size);
            Prune((await settings.GetAsync<BackupSettings>(ct)).RetentionCount);
            return info;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Deletes the oldest backups so at most <paramref name="keep"/> (at least one) remain.</summary>
    public void Prune(int keep)
    {
        foreach (var old in List().Skip(Math.Max(1, keep)))
        {
            try
            {
                File.Delete(Path.Combine(paths.BackupsDirectory, old.Name));
                logger.LogInformation("Deleted old backup {Name}", old.Name);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete old backup {Name}", old.Name);
            }
        }
    }

    /// <summary>Newest scheduled backup, used to keep the weekly schedule across restarts.</summary>
    public DateTimeOffset? LastScheduled() => List().FirstOrDefault(b => b.Type == BackupType.Scheduled)?.Created;

    /// <summary>Copies an uploaded backup to disk, validates it and stages it for the next start.</summary>
    /// <returns>An error message for the user, or null when the restore is staged.</returns>
    public async Task<string?> StageRestoreAsync(Stream upload, CancellationToken ct)
    {
        var partial = paths.PendingRestoreFile + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await upload.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes) return "The file is too large";
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                if (total == 0) return "The file is empty";
            }

            return Stage(partial);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Stages one of the backups in the folder for the next start.</summary>
    public async Task<string?> StageRestoreAsync(string name, CancellationToken ct)
    {
        if (PathOf(name) is not { } source) return null;
        var partial = paths.PendingRestoreFile + ".partial";
        try
        {
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var to = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await from.CopyToAsync(to, ct);
            }

            return Stage(partial);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    public bool CancelRestore()
    {
        if (!File.Exists(paths.PendingRestoreFile)) return false;
        File.Delete(paths.PendingRestoreFile);
        return true;
    }

    private string? Stage(string partial)
    {
        var (_, error) = BackupArchive.Validate(partial, Provider);
        if (error is not null) return error;
        File.Move(partial, paths.PendingRestoreFile, overwrite: true);
        logger.LogWarning("A backup was staged; it is restored the next time Krakarr starts");
        return null;
    }

    private string UniqueName(DateTimeOffset now, BackupType type)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var stem = $"{Prefix}{stamp}_{type.ToString().ToLowerInvariant()}";
        // Next sequence after any backup of the same second (never reuse a pruned name: it would sort before newer ones).
        var sequence = List().Where(b => b.Name.StartsWith($"{Prefix}{stamp}_", StringComparison.Ordinal)).Select(b => Sequence(b.Name)).DefaultIfEmpty(0).Max() + 1;
        return sequence == 1 ? stem + ".zip" : $"{stem}_{sequence}.zip";
    }

    private async Task WriteArchiveAsync(string path, BackupType type, DateTimeOffset now, CancellationToken ct)
    {
        string? dbCopy = null;
        try
        {
            if (Provider == "sqlite")
            {
                // VACUUM INTO writes a consistent, compacted copy while the app keeps running (readers and writers aren't blocked for long).
                dbCopy = Path.Combine(paths.BackupsDirectory, $".{Guid.NewGuid():N}.db");
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {dbCopy}", ct);
            }

            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using var zip = new ZipArchive(file, ZipArchiveMode.Create);

            var manifest = new BackupManifest(BackupArchive.FormatVersion, SystemEndpoints.Version, Provider, now, type.ToString());
            await using (var entry = await zip.CreateEntry(BackupArchive.ManifestEntry).OpenAsync(ct))
            {
                await JsonSerializer.SerializeAsync(entry, manifest, BackupArchive.Json, ct);
            }

            if (dbCopy is not null)
            {
                await AddFileAsync(zip, dbCopy, BackupArchive.DatabaseEntry, ct);
            }
            else
            {
                await using var entry = await zip.CreateEntry(BackupArchive.SettingsEntry).OpenAsync(ct);
                var rows = db.Settings.AsNoTracking().OrderBy(s => s.Key).Select(s => new BackupRestore.SettingRow(s.Key, s.Value)).AsAsyncEnumerable();
                await JsonSerializer.SerializeAsync(entry, rows, BackupArchive.Json, ct);
            }

            if (File.Exists(paths.ConfigFile)) await AddFileAsync(zip, paths.ConfigFile, BackupArchive.ConfigEntry, ct);
            if (Directory.Exists(paths.KeysDirectory))
            {
                foreach (var key in Directory.EnumerateFiles(paths.KeysDirectory, "*.xml"))
                {
                    await AddFileAsync(zip, key, BackupArchive.KeysPrefix + Path.GetFileName(key), ct);
                }
            }
        }
        finally
        {
            if (dbCopy is not null) File.Delete(dbCopy);
        }
    }

    private static async Task AddFileAsync(ZipArchive zip, string source, string entryName, CancellationToken ct)
    {
        await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        await using var to = await zip.CreateEntry(entryName, CompressionLevel.Optimal).OpenAsync(ct);
        await from.CopyToAsync(to, ct);
    }
}
