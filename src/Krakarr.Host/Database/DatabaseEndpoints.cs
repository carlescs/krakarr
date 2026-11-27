using System.Data.Common;
using Krakarr.Core.Configuration;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Krakarr.Host.Database;

/// <summary>
/// The database to switch to, as the UI asks for it: server fields for PostgreSQL, a file name for SQLite. With
/// <see cref="DeleteSource"/>, the current SQLite file is deleted once the data has been copied.
/// </summary>
public sealed record DatabaseTargetRequest(
    string Provider, string? Host = null, int? Port = null, string? Database = null, string? Username = null, string? Password = null,
    string? File = null, bool DeleteSource = false);

public sealed record DatabaseTestResult(bool Ok, bool Empty, string Message);

/// <summary>An environment variable to set by hand, when environment variables choose the database.</summary>
public sealed record EnvironmentSetting(string Name, string Value);

/// <summary>Tests a database to switch to and stages the switch for the next start (<see cref="DatabaseSwitch"/>).</summary>
public sealed class DatabaseSwitchService(KrakarrPaths paths, DatabaseSwitchStatus status, IConfiguration configuration)
{
    private const string PasswordPlaceholder = "<password>";

    public IReadOnlyList<string> Pinned => DatabaseSwitch.PinnedSettings((IConfigurationRoot)configuration, paths.ConfigFile);

    public object Info()
    {
        var plan = DatabaseSwitch.ReadPlan(paths);
        return new
        {
            status.Current.Provider,
            Location = status.Current.Describe(),
            Pinned,
            Pending = plan is null ? null : new
            {
                plan.Target.Provider, Location = plan.Target.Describe(), plan.Created,
                DeleteSource = plan.DeleteSource ? plan.Source.SqliteFile() : null,
            },
            PendingEnvironment = plan is null ? null : EnvironmentFor(plan.Target),
            LastSwitch = status.Last,
            OldDatabase = OldDatabaseInfo(),
        };
    }

    /// <summary>The SQLite file left behind by a switch, while it still exists and Krakarr runs elsewhere.</summary>
    private object? OldDatabaseInfo()
    {
        if (DatabaseSwitch.ReadOldDatabase(paths) is not { } old || !File.Exists(old.File) || InUse(old.File)) return null;
        var size = DatabaseSwitch.SqliteFiles(old.File).Where(File.Exists).Sum(f => new FileInfo(f).Length);
        return new { old.File, Size = size, old.SwitchedAt };
    }

    /// <summary>Krakarr runs on the file, or a staged switch reads or writes it.</summary>
    private bool InUse(string file)
    {
        var plan = DatabaseSwitch.ReadPlan(paths);
        return status.Current.SqliteFile() == file || plan?.Source.SqliteFile() == file || plan?.Target.SqliteFile() == file;
    }

    /// <summary>Deletes the SQLite file Krakarr used before the switch. Returns null, or why it didn't.</summary>
    public (int Status, string Error)? DeleteOldDatabase()
    {
        if (DatabaseSwitch.ReadOldDatabase(paths) is not { } old || !File.Exists(old.File))
        {
            DatabaseSwitch.ForgetOldDatabase(paths);
            return (StatusCodes.Status404NotFound, "There is no old database to delete");
        }

        if (InUse(old.File)) return (StatusCodes.Status409Conflict, "Krakarr is using that database, or a staged switch is");
        if (DatabaseSwitch.TryDeleteSqliteFile(old.File) is { } error) return (StatusCodes.Status409Conflict, $"Could not delete {old.File}: {error}");
        DatabaseSwitch.ForgetOldDatabase(paths);
        return null;
    }

    /// <summary>The database a request describes, or why it can't be used.</summary>
    public (DatabaseTarget? Target, string? Error) Resolve(DatabaseTargetRequest request)
    {
        switch (new DatabaseOptions { Provider = request.Provider ?? string.Empty }.ProviderKind)
        {
            case DatabaseTarget.Postgres:
                if (string.IsNullOrWhiteSpace(request.Host)) return (null, "Enter the PostgreSQL server");
                if (string.IsNullOrWhiteSpace(request.Database)) return (null, "Enter the database name");
                if (string.IsNullOrWhiteSpace(request.Username)) return (null, "Enter the user name");
                if (request.Port is < 1 or > 65535) return (null, "The port must be between 1 and 65535");
                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = request.Host.Trim(),
                    Port = request.Port ?? 5432,
                    Database = request.Database.Trim(),
                    Username = request.Username.Trim(),
                };
                if (!string.IsNullOrEmpty(request.Password)) builder.Password = request.Password;
                return (new DatabaseTarget(DatabaseTarget.Postgres, builder.ConnectionString), null);

            case DatabaseTarget.Sqlite:
                // A file name in the config folder, which is the volume Krakarr keeps its data in.
                var name = request.File?.Trim();
                if (string.IsNullOrEmpty(name)) return (null, "Enter the database file name");
                if (name is "." or ".." || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    return (null, "Enter a file name without folders; the file goes in the config folder");
                }

                var file = Path.Combine(paths.ConfigDirectory, name);
                return (new DatabaseTarget(DatabaseTarget.Sqlite, new SqliteConnectionStringBuilder { DataSource = file }.ToString()), null);

            default:
                return (null, "The provider must be sqlite or postgres");
        }
    }

    /// <summary>Connects to the database and checks that it holds no Krakarr data. Changes nothing.</summary>
    public async Task<DatabaseTestResult> TestAsync(DatabaseTarget target, CancellationToken ct)
    {
        if (target.SameAs(status.Current)) return new(false, false, "That's the database Krakarr is using now");
        try
        {
            return target.Provider == DatabaseTarget.Postgres ? await TestPostgresAsync(target, ct) : await TestSqliteAsync(target, ct);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or ArgumentException or TimeoutException or IOException)
        {
            return new(false, false, ex.Message);
        }
    }

    private static async Task<DatabaseTestResult> TestPostgresAsync(DatabaseTarget target, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Timeout = 10, Pooling = false };
        target = target with { ConnectionString = builder.ConnectionString };
        string version;
        try
        {
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync(ct);
            version = connection.PostgreSqlVersion.ToString();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
        {
            return new(true, true, $"The server answered, but the database \"{builder.Database}\" doesn't exist yet. Krakarr creates it "
                + "when it switches, which needs a user allowed to create databases (or create it yourself first).");
        }

        await using var db = target.CreateContext();
        return await DatabaseCopier.IsEmptyAsync(db, ct)
            ? new(true, true, $"Connected to PostgreSQL {version}. The database is empty, ready for Krakarr's data.")
            : new(true, false, $"Connected to PostgreSQL {version}, but the database already holds Krakarr data. Pick an empty database.");
    }

    private static async Task<DatabaseTestResult> TestSqliteAsync(DatabaseTarget target, CancellationToken ct)
    {
        var file = target.SqliteFile()!;
        if (!File.Exists(file)) return new(true, true, $"Krakarr will create {file}.");

        var readOnly = target.ReadOnly();
        readOnly = readOnly with { ConnectionString = new SqliteConnectionStringBuilder(readOnly.ConnectionString) { Pooling = false }.ToString() };
        await using var db = readOnly.CreateContext();
        return await DatabaseCopier.IsEmptyAsync(db, ct)
            ? new(true, true, $"{file} exists but holds no Krakarr data; it will be filled.")
            : new(true, false, $"{file} already holds Krakarr data. Pick another file name, or move that file away first.");
    }

    /// <summary>Tests the database and, when it's usable, stages the switch for the next start.</summary>
    /// <param name="deleteSource">Delete the current SQLite file after the copy (ignored on PostgreSQL).</param>
    public async Task<(DatabaseTestResult Test, IReadOnlyList<EnvironmentSetting> Environment)> StageAsync(
        DatabaseTarget target, bool deleteSource, CancellationToken ct)
    {
        var test = await TestAsync(target, ct);
        if (!test.Ok || !test.Empty) return (test, []);
        deleteSource &= status.Current.SqliteFile() is not null;
        DatabaseSwitch.WritePlan(paths, new DatabaseSwitchPlan(status.Current, target, DateTimeOffset.UtcNow, deleteSource));
        return (test, EnvironmentFor(target));
    }

    /// <summary>What to set when environment variables choose the database (none otherwise), password left out.</summary>
    private IReadOnlyList<EnvironmentSetting> EnvironmentFor(DatabaseTarget target)
    {
        if (Pinned.Count == 0) return [];
        var connection = target.ConnectionString;
        if (target.Provider == DatabaseTarget.Postgres)
        {
            var builder = new NpgsqlConnectionStringBuilder(connection);
            if (!string.IsNullOrEmpty(builder.Password)) builder.Password = PasswordPlaceholder;
            connection = builder.ConnectionString;
        }

        return
        [
            new($"{DatabaseOptions.SectionName}__{nameof(DatabaseOptions.Provider)}", target.Provider),
            new($"{DatabaseOptions.SectionName}__{nameof(DatabaseOptions.ConnectionString)}", connection),
        ];
    }
}

public static class DatabaseEndpoints
{
    public static IServiceCollection AddKrakarrDatabaseSwitch(this IServiceCollection services, DatabaseSwitchStatus status)
    {
        services.AddSingleton(status);
        services.AddScoped<DatabaseSwitchService>();
        services.AddScoped<DatabaseDetailsService>();
        return services;
    }

    /// <summary>Admin only (see <c>RoleAccess</c>): it handles database credentials.</summary>
    public static IEndpointRouteBuilder MapDatabaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system/database").WithTags("Database");

        group.MapGet("/", (DatabaseSwitchService service) => service.Info());

        // Version, size, connection fields and row counts of the database Krakarr runs on.
        group.MapGet("/details", (DatabaseDetailsService service, CancellationToken ct) => service.GetAsync(ct));

        group.MapPost("/test", async (DatabaseTargetRequest request, DatabaseSwitchService service, CancellationToken ct) =>
        {
            var (target, error) = service.Resolve(request);
            return target is null
                ? Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await service.TestAsync(target, ct));
        });

        group.MapPost("/switch", async (DatabaseTargetRequest request, DatabaseSwitchService service, CancellationToken ct) =>
        {
            var (target, error) = service.Resolve(request);
            if (target is null) return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
            var (test, environment) = await service.StageAsync(target, request.DeleteSource, ct);
            if (!test.Ok || !test.Empty) return Results.Problem(test.Message, statusCode: StatusCodes.Status400BadRequest);
            return Results.Accepted(value: new { test.Message, Environment = environment });
        });

        group.MapDelete("/switch", (KrakarrPaths paths) => DatabaseSwitch.CancelPlan(paths) ? Results.NoContent() : Results.NotFound());

        // The SQLite file left behind by a switch, deleted once the user is happy with the new database.
        group.MapDelete("/old", (DatabaseSwitchService service) =>
            service.DeleteOldDatabase() is { } failed ? Results.Problem(failed.Error, statusCode: failed.Status) : Results.NoContent());

        return app;
    }
}
