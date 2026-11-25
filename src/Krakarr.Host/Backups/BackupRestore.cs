using System.IO.Compression;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Backups;

/// <summary>Result of restoring a backup at startup, shown on the Backups page until the next restart.</summary>
public sealed record RestoreOutcome(bool Succeeded, string Message, DateTimeOffset At);

/// <summary>Holds the outcome of the restore done at startup, if any.</summary>
public sealed class RestoreStatus(RestoreOutcome? last)
{
    public RestoreOutcome? Last { get; } = last;
}

/// <summary>
/// Applies a staged backup (<see cref="KrakarrPaths.PendingRestoreFile"/>) when Krakarr starts, before anything opens
/// the database. The files it replaces are kept next to them with a <c>.pre-restore</c> suffix.
/// </summary>
public static class BackupRestore
{
    public const string PreRestoreSuffix = ".pre-restore";
    private const string FailedFileName = "restore-failed.zip";
    private const string PendingSettingsFileName = "restore-settings.json";

    /// <param name="paths">Krakarr's folders.</param>
    /// <param name="provider">Database provider in use (<see cref="DatabaseOptions.ProviderKind"/>).</param>
    /// <param name="sqlitePath">Resolves the SQLite database file; called after config.json has been restored.</param>
    /// <param name="configRestored">Called once config.json has been replaced, so the host can reload its configuration.</param>
    /// <returns>Null when no restore was pending.</returns>
    public static RestoreOutcome? ApplyPending(KrakarrPaths paths, string provider, Func<string?> sqlitePath, Action? configRestored = null)
    {
        var pending = paths.PendingRestoreFile;
        if (!File.Exists(pending)) return null;

        try
        {
            string? error;
            using (var zip = ZipFile.OpenRead(pending))
            {
                (_, error) = BackupArchive.Validate(zip, provider);
                if (error is null) error = Apply(zip, paths, provider, sqlitePath, configRestored);
            }

            if (error is not null) return Fail(paths, error);
            File.Delete(pending);
            return new RestoreOutcome(true, "The backup was restored", DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException)
        {
            return Fail(paths, ex.Message);
        }
    }

    private static RestoreOutcome Fail(KrakarrPaths paths, string error)
    {
        // Keep the archive for inspection, but don't try it again on every start.
        try
        {
            File.Move(paths.PendingRestoreFile, Path.Combine(paths.ConfigDirectory, FailedFileName), overwrite: true);
        }
        catch (IOException)
        {
        }

        return new RestoreOutcome(false, $"The backup could not be restored: {error}", DateTimeOffset.UtcNow);
    }

    private static string? Apply(ZipArchive zip, KrakarrPaths paths, string provider, Func<string?> sqlitePath, Action? configRestored)
    {
        // Check the database before touching anything, so a broken backup leaves the current install as it was.
        string? stagedDb = null;
        string? dbPath = null;
        if (provider == "sqlite")
        {
            dbPath = sqlitePath();
            if (string.IsNullOrEmpty(dbPath)) return "the SQLite database path could not be determined";
            stagedDb = dbPath + ".restore";
            zip.GetEntry(BackupArchive.DatabaseEntry)!.ExtractToFile(stagedDb, overwrite: true);
            if (QuickCheck(stagedDb) is { } problem)
            {
                File.Delete(stagedDb);
                return $"the database in the backup is damaged ({problem})";
            }
        }

        if (zip.GetEntry(BackupArchive.ConfigEntry) is { } config)
        {
            KeepAside(paths.ConfigFile);
            config.ExtractToFile(paths.ConfigFile, overwrite: true);
            configRestored?.Invoke();
            if (provider == "sqlite" && sqlitePath() is { Length: > 0 } restoredPath && restoredPath != dbPath)
            {
                // The restored config.json points at another database file: put the backup there.
                File.Move(stagedDb!, restoredPath + ".restore", overwrite: true);
                (dbPath, stagedDb) = (restoredPath, restoredPath + ".restore");
            }
        }

        var keys = zip.Entries.Where(e => BackupArchive.IsKeyEntry(e.FullName)).ToList();
        if (keys.Count > 0)
        {
            var aside = paths.KeysDirectory + PreRestoreSuffix;
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            if (Directory.Exists(paths.KeysDirectory)) Directory.Move(paths.KeysDirectory, aside);
            Directory.CreateDirectory(paths.KeysDirectory);
            foreach (var key in keys)
            {
                key.ExtractToFile(Path.Combine(paths.KeysDirectory, Path.GetFileName(key.FullName)), overwrite: true);
            }
        }

        if (stagedDb is not null && dbPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            // "<db>.pre-restore" with its "-wal" next to it is still a complete SQLite database (same naming rule).
            KeepAside(dbPath);
            KeepAside(dbPath + "-wal", dbPath + PreRestoreSuffix + "-wal");
            File.Delete(dbPath + "-shm");
            File.Delete(dbPath + PreRestoreSuffix + "-shm");
            File.Move(stagedDb, dbPath);
        }

        if (provider != "sqlite")
        {
            // Settings go into PostgreSQL once the host has migrated it (ImportPendingSettingsAsync).
            zip.GetEntry(BackupArchive.SettingsEntry)!.ExtractToFile(Path.Combine(paths.ConfigDirectory, PendingSettingsFileName), overwrite: true);
        }

        return null;
    }

    /// <summary>Writes settings restored from a PostgreSQL backup into the database. Run after migrations.</summary>
    public static async Task ImportPendingSettingsAsync(KrakarrDbContext db, KrakarrPaths paths, SettingsCache cache, CancellationToken ct = default)
    {
        var file = Path.Combine(paths.ConfigDirectory, PendingSettingsFileName);
        if (!File.Exists(file)) return;

        List<SettingRow>? rows;
        await using (var stream = File.OpenRead(file))
        {
            rows = await JsonSerializer.DeserializeAsync<List<SettingRow>>(stream, BackupArchive.Json, ct);
        }

        foreach (var row in rows ?? [])
        {
            if (string.IsNullOrEmpty(row.Key)) continue;
            var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == row.Key, ct);
            if (entry is null) db.Settings.Add(new SettingEntry { Key = row.Key, Value = row.Value });
            else entry.Value = row.Value;
        }

        await db.SaveChangesAsync(ct);
        cache.Clear();
        File.Delete(file);
    }

    internal sealed record SettingRow(string Key, string Value);

    private static string? QuickCheck(string path)
    {
        try
        {
            return RunQuickCheck(path);
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    private static string? RunQuickCheck(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var connection = new SqliteConnection(cs);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        var result = command.ExecuteScalar() as string;
        return result == "ok" ? null : result ?? "no result";
    }

    private static void KeepAside(string path, string? aside = null)
    {
        if (File.Exists(path)) File.Move(path, aside ?? path + PreRestoreSuffix, overwrite: true);
    }

    /// <summary>The file a SQLite connection string points at, or null for in-memory databases.</summary>
    public static string? SqliteFile(string connectionString)
    {
        var source = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return string.IsNullOrEmpty(source) || source == ":memory:" || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetFullPath(source);
    }
}
