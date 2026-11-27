using System.Data.Common;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Krakarr.Host.Database;

/// <summary>How many rows the main tables hold.</summary>
public sealed record DatabaseRowCounts(
    int Movies, int MovieFiles, int Series, int Episodes, int EpisodeFiles, int SubtitleFiles, int History, int Users);

/// <summary>
/// The database Krakarr runs on, looked at more closely: server version, size, connection fields (never the password),
/// schema version and row counts. A value Krakarr couldn't read is null.
/// </summary>
public sealed record DatabaseDetails(
    string Provider,
    string? Version,
    long? Size,
    string? File,
    string? JournalMode,
    string? Host,
    int? Port,
    string? Database,
    string? Username,
    string? SslMode,
    string? LastMigration,
    int AppliedMigrations,
    int PendingMigrations,
    DatabaseRowCounts? Rows);

/// <summary>Reads <see cref="DatabaseDetails"/> for the current database, with cheap aggregate queries only.</summary>
public sealed class DatabaseDetailsService(DatabaseSwitchStatus status, KrakarrDbContext db, ILogger<DatabaseDetailsService> logger)
{
    public async Task<DatabaseDetails> GetAsync(CancellationToken ct)
    {
        var current = status.Current;
        var postgres = current.Provider == DatabaseTarget.Postgres;
        NpgsqlConnectionStringBuilder? builder = null;
        if (postgres)
        {
            try { builder = new NpgsqlConnectionStringBuilder(current.ConnectionString); }
            catch (ArgumentException) { }
        }

        var file = current.SqliteFile();
        string? version = null, journalMode = null;
        long? size = null;
        try
        {
            if (postgres)
            {
                version = await ScalarAsync<string>("SHOW server_version", ct);
                size = await ScalarAsync<long?>("SELECT pg_database_size(current_database())", ct);
            }
            else
            {
                version = await ScalarAsync<string>("SELECT sqlite_version()", ct);
                journalMode = await ScalarAsync<string>("PRAGMA journal_mode", ct);
            }
        }
        catch (DbException ex)
        {
            logger.LogDebug(ex, "Could not read the database version or size");
        }

        if (file is not null)
        {
            size = DatabaseSwitch.SqliteFiles(file).Where(System.IO.File.Exists).Sum(f => new FileInfo(f).Length);
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).Count();

        DatabaseRowCounts? rows = null;
        try
        {
            rows = new DatabaseRowCounts(
                await db.Movies.CountAsync(ct),
                await db.MovieFiles.CountAsync(ct),
                await db.Series.CountAsync(ct),
                await db.Episodes.CountAsync(ct),
                await db.EpisodeFiles.CountAsync(ct),
                await db.SubtitleFiles.CountAsync(ct),
                await db.History.CountAsync(ct),
                await db.Users.CountAsync(ct));
        }
        catch (DbException ex)
        {
            logger.LogDebug(ex, "Could not count the database rows");
        }

        return new DatabaseDetails(
            current.Provider, version, size, file, journalMode,
            builder?.Host, builder?.Port, builder?.Database, builder?.Username, builder?.SslMode.ToString(),
            applied.LastOrDefault(), applied.Count, pending, rows);
    }

    private async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? default : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
