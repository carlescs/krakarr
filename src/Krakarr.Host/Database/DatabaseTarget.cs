using Krakarr.Data;
using Krakarr.Data.Postgres;
using Krakarr.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Krakarr.Host.Database;

/// <summary>A database Krakarr can run on: the provider kind ("sqlite" or "postgres") and its connection string.</summary>
public sealed record DatabaseTarget(string Provider, string ConnectionString)
{
    public const string Sqlite = "sqlite";
    public const string Postgres = "postgres";

    /// <summary>The database the configuration points at, with SQLite defaulting to krakarr.db in the config folder.</summary>
    public static DatabaseTarget From(DatabaseOptions options, string configDirectory) => options.ProviderKind switch
    {
        Postgres => new(Postgres, options.ConnectionString
            ?? throw new InvalidOperationException("Database:ConnectionString is required for PostgreSQL")),
        Sqlite => new(Sqlite, options.ConnectionString ?? $"Data Source={Path.Combine(configDirectory, "krakarr.db")}"),
        _ => throw new InvalidOperationException($"Unknown database provider '{options.Provider}' (use sqlite or postgres)"),
    };

    /// <summary>Where the data lives, without the password: the SQLite file, or user@host:port/database.</summary>
    public string Describe()
    {
        try
        {
            if (Provider == Sqlite) return SqliteFile() ?? "(in memory)";
            var b = new NpgsqlConnectionStringBuilder(ConnectionString);
            return $"{b.Username}@{b.Host}:{b.Port}/{b.Database}";
        }
        catch (ArgumentException)
        {
            return "(invalid connection string)";
        }
    }

    /// <summary>True when both point at the same database (same file, or same server, database and user).</summary>
    public bool SameAs(DatabaseTarget other)
    {
        if (Provider != other.Provider) return false;
        try
        {
            if (Provider == Sqlite)
            {
                return SqliteFile() is { } file ? file == other.SqliteFile() : ConnectionString == other.ConnectionString;
            }

            var a = new NpgsqlConnectionStringBuilder(ConnectionString);
            var b = new NpgsqlConnectionStringBuilder(other.ConnectionString);
            return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port
                && a.Database == b.Database && a.Username == b.Username;
        }
        catch (ArgumentException)
        {
            return ConnectionString == other.ConnectionString;
        }
    }

    /// <summary>The SQLite database file, or null for in-memory databases and PostgreSQL.</summary>
    public string? SqliteFile() =>
        Provider == Sqlite ? Backups.BackupRestore.SqliteFile(ConnectionString) : null;

    public DbContextOptionsBuilder Configure(DbContextOptionsBuilder builder) => Provider switch
    {
        Postgres => builder.UseKrakarrPostgres(ConnectionString),
        Sqlite => builder.UseKrakarrSqlite(ConnectionString),
        _ => throw new InvalidOperationException($"Unknown database provider '{Provider}'"),
    };

    /// <summary>A context of its own on this database, outside dependency injection (start-up, connection tests).</summary>
    public KrakarrDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<KrakarrDbContext>();
        Configure(builder);
        return new KrakarrDbContext(builder.Options);
    }

    /// <summary>This SQLite database opened read-only, so looking at it never creates the file.</summary>
    public DatabaseTarget ReadOnly() => Provider == Sqlite
        ? this with { ConnectionString = new SqliteConnectionStringBuilder(ConnectionString) { Mode = SqliteOpenMode.ReadOnly }.ToString() }
        : this;

    // Keep the connection string (it holds the password) out of logs and exception messages.
    public override string ToString() => $"{Provider} {Describe()}";
}
