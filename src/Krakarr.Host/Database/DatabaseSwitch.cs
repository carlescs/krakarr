using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krakarr.Core.Configuration;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Npgsql;

namespace Krakarr.Host.Database;

/// <summary>A switch to another database, staged from the UI and carried out on the next start.</summary>
public sealed record DatabaseSwitchPlan(DatabaseTarget Source, DatabaseTarget Target, DateTimeOffset Created);

/// <summary>Result of the switch done at start-up, shown on the Database page until the next restart.</summary>
public sealed record DatabaseSwitchOutcome(bool Succeeded, string Message, DateTimeOffset At, IReadOnlyList<TableCopy> Tables);

/// <summary>Holds the outcome of the switch done at start-up, if any, and the database Krakarr runs on.</summary>
public sealed class DatabaseSwitchStatus(DatabaseTarget current, DatabaseSwitchOutcome? last)
{
    public DatabaseTarget Current { get; } = current;
    public DatabaseSwitchOutcome? Last { get; } = last;
}

/// <summary>
/// Moves Krakarr to another database. The UI stages a plan (<see cref="KrakarrPaths.PendingDatabaseSwitchFile"/>), and
/// the next start copies every table into the new database before anything else opens either of them, then points
/// config.json at it. The old database is left as it was.
/// </summary>
/// <remarks>
/// When environment variables choose the database, Krakarr can't change them: the user sets them to the new database
/// and restarts, and the copy runs then. Until they do, Krakarr keeps running on the old database and the plan waits.
/// </remarks>
public static class DatabaseSwitch
{
    public const string FailedFileName = "database-switch-failed.json";
    public const string PreSwitchSuffix = ".pre-switch";

    /// <summary>How long start-up waits for the new database server to accept connections (e.g. a container still starting).</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed record Result(DatabaseSwitchOutcome Outcome, DatabaseTarget Use);

    public static DatabaseSwitchPlan? ReadPlan(KrakarrPaths paths)
    {
        if (!File.Exists(paths.PendingDatabaseSwitchFile)) return null;
        try
        {
            using var stream = File.OpenRead(paths.PendingDatabaseSwitchFile);
            return JsonSerializer.Deserialize<DatabaseSwitchPlan>(stream, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the plan readable only by Krakarr's user: it holds the new database's password.</summary>
    public static void WritePlan(KrakarrPaths paths, DatabaseSwitchPlan plan) =>
        WritePrivate(paths.PendingDatabaseSwitchFile, JsonSerializer.SerializeToUtf8Bytes(plan, Json));

    public static bool CancelPlan(KrakarrPaths paths)
    {
        if (!File.Exists(paths.PendingDatabaseSwitchFile)) return false;
        File.Delete(paths.PendingDatabaseSwitchFile);
        return true;
    }

    /// <summary>
    /// Carries out a staged switch. Returns null when none is staged; otherwise the outcome and the database to run on
    /// (config.json has been rewritten when that changed, but the caller's configuration isn't reloaded).
    /// </summary>
    /// <param name="configured">The database the configuration points at right now.</param>
    /// <param name="pinned">Database settings that environment variables (or another source after config.json) fix.</param>
    public static async Task<Result?> ApplyPendingAsync(
        KrakarrPaths paths, DatabaseTarget configured, IReadOnlyList<string> pinned, ILogger logger, CancellationToken ct = default)
    {
        if (!File.Exists(paths.PendingDatabaseSwitchFile)) return null;
        var plan = ReadPlan(paths);
        if (plan is null) return Fail(paths, configured, "the staged switch could not be read");

        var onTarget = configured.SameAs(plan.Target);
        if (!onTarget && !configured.SameAs(plan.Source))
        {
            return Fail(paths, configured, $"Krakarr is configured for {configured.Describe()}, which is neither the database it was "
                + $"switching from ({plan.Source.Describe()}) nor the one it was switching to ({plan.Target.Describe()})");
        }

        if (!onTarget && pinned.Count > 0)
        {
            // The plan stays: the copy runs once the environment points at the new database.
            var waiting = $"Waiting to switch to {plan.Target.Describe()}: {string.Join(" and ", pinned)} still choose the current "
                + "database. Change them to the new database and restart Krakarr.";
            logger.LogWarning("{Message}", waiting);
            return new Result(new DatabaseSwitchOutcome(false, waiting, DateTimeOffset.UtcNow, []), configured);
        }

        logger.LogWarning("Switching the database from {Source} to {Target}", plan.Source, plan.Target);
        IReadOnlyList<TableCopy> tables;
        JsonObject? config = null;
        try
        {
            // Read config.json first: a file that can't be rewritten must stop the switch before any data moves.
            if (!onTarget) config = ReadConfig(paths.ConfigFile);
            tables = await CopyAsync(plan.Source, plan.Target, logger, ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException or ArgumentException
            or IOException or TimeoutException or JsonException)
        {
            logger.LogError(ex, "Switching the database to {Target} failed", plan.Target);
            var error = ex is DbUpdateException { InnerException: { } inner } ? inner.Message : ex.Message;
            if (onTarget)
            {
                // The environment already points at the new database, which is empty again: run on the old one, and
                // keep the plan so the next start tries again rather than starting from an empty database.
                return new Result(new DatabaseSwitchOutcome(false,
                    $"The data could not be copied to {plan.Target.Describe()}: {error}. Krakarr is still running on "
                    + $"{plan.Source.Describe()} and tries again on the next start; cancel the switch to stop.", DateTimeOffset.UtcNow, []),
                    plan.Source);
            }

            return Fail(paths, configured, $"the data could not be copied to {plan.Target.Describe()}: {error}");
        }

        if (config is not null) WriteConfig(paths.ConfigFile, config, plan.Target);
        File.Delete(paths.PendingDatabaseSwitchFile);

        var rows = tables.Sum(t => t.Rows);
        var truncated = tables.Sum(t => t.Truncated);
        var message = $"Switched to {plan.Target.Describe()}: copied {rows:N0} rows in {tables.Count} tables. "
            + $"The old database ({plan.Source.Describe()}) was left as it was.";
        if (truncated > 0) message += $" {truncated:N0} values longer than their column allows were shortened.";
        logger.LogWarning("{Message}", message);
        return new Result(new DatabaseSwitchOutcome(true, message, DateTimeOffset.UtcNow, tables), plan.Target);
    }

    private static Result Fail(KrakarrPaths paths, DatabaseTarget configured, string error)
    {
        // Keep the plan for inspection, but don't try it again on every start.
        try
        {
            File.Move(paths.PendingDatabaseSwitchFile, Path.Combine(paths.ConfigDirectory, FailedFileName), overwrite: true);
        }
        catch (IOException)
        {
        }

        return new Result(new DatabaseSwitchOutcome(false,
            $"The database switch failed: {error}. Krakarr is still running on {configured.Describe()}.", DateTimeOffset.UtcNow, []), configured);
    }

    private static async Task<IReadOnlyList<TableCopy>> CopyAsync(DatabaseTarget source, DatabaseTarget target, ILogger logger, CancellationToken ct)
    {
        if (target.SqliteFile() is { } file) Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        await using var from = source.CreateContext();
        await using var to = target.CreateContext();
        // Both must have the schema this version expects, and the source may be from before an upgrade.
        await from.Database.MigrateAsync(ct);
        await MigrateWhenReachableAsync(to, logger, ct);
        if (!await DatabaseCopier.IsEmptyAsync(to, ct))
        {
            throw new InvalidOperationException("it already holds Krakarr data; switch to an empty database");
        }

        return await DatabaseCopier.CopyAsync(from, to, table => logger.LogInformation("Copying {Table}", table), ct);
    }

    /// <summary>Creates and migrates the database, waiting a while for a server that isn't accepting connections yet.</summary>
    private static async Task MigrateWhenReachableAsync(KrakarrDbContext db, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        while (true)
        {
            try
            {
                await db.Database.MigrateAsync(ct);
                return;
            }
            catch (NpgsqlException ex) when (ex.IsTransient && DateTimeOffset.UtcNow < deadline)
            {
                logger.LogWarning("The new database isn't reachable yet ({Error}); retrying", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>Sets Database:Provider and Database:ConnectionString in config.json, keeping the rest of it.</summary>
    public static void PointConfigAt(string configFile, DatabaseTarget target) => WriteConfig(configFile, ReadConfig(configFile), target);

    private static JsonObject ReadConfig(string configFile)
    {
        if (!File.Exists(configFile)) return [];
        var text = File.ReadAllText(configFile);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidOperationException("config.json is not a JSON object");
    }

    private static void WriteConfig(string configFile, JsonObject root, DatabaseTarget target)
    {
        // The previous file stays next to it, to go back by hand.
        if (File.Exists(configFile)) File.Copy(configFile, configFile + PreSwitchSuffix, overwrite: true);

        // Configuration keys are case-insensitive: replace any spelling of them.
        var section = Take(root, DatabaseOptions.SectionName) as JsonObject ?? [];
        Take(section, nameof(DatabaseOptions.Provider));
        Take(section, nameof(DatabaseOptions.ConnectionString));
        section[nameof(DatabaseOptions.Provider)] = target.Provider;
        section[nameof(DatabaseOptions.ConnectionString)] = target.ConnectionString;
        root[DatabaseOptions.SectionName] = section;

        var temp = configFile + ".tmp";
        WritePrivate(temp, JsonSerializer.SerializeToUtf8Bytes(root, Json));
        File.Move(temp, configFile, overwrite: true);
    }

    private static JsonNode? Take(JsonObject obj, string key)
    {
        JsonNode? found = null;
        foreach (var name in obj.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            found ??= obj[name];
            obj.Remove(name);
        }

        return found;
    }

    private static void WritePrivate(string path, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(content);
    }

    /// <summary>
    /// Database settings that a configuration source after config.json sets (normally environment variables), so
    /// rewriting config.json wouldn't change the database. Named the way the user sets them.
    /// </summary>
    public static IReadOnlyList<string> PinnedSettings(IConfigurationRoot configuration, string configFile)
    {
        var providers = configuration.Providers.ToList();
        var configIndex = providers.FindLastIndex(p => p is JsonConfigurationProvider json
            && json.Source.Path is { } path && json.Source.FileProvider?.GetFileInfo(path).PhysicalPath is { } physical
            && string.Equals(Path.GetFullPath(physical), Path.GetFullPath(configFile), StringComparison.Ordinal));

        var pinned = new List<string>();
        foreach (var key in new[] { nameof(DatabaseOptions.Provider), nameof(DatabaseOptions.ConnectionString) })
        {
            var fullKey = $"{DatabaseOptions.SectionName}:{key}";
            // In-memory sources are Krakarr's own overrides (and test settings), not something the user set.
            var source = providers.Skip(configIndex + 1).LastOrDefault(p => p is not MemoryConfigurationProvider && p.TryGet(fullKey, out _));
            if (source is null) continue;
            pinned.Add(source is EnvironmentVariablesConfigurationProvider ? $"{DatabaseOptions.SectionName}__{key}" : fullKey);
        }

        return pinned;
    }
}
