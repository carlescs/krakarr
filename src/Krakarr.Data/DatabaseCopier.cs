using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Krakarr.Data;

/// <summary>Rows copied into one table, and how many of its text values had to be shortened to fit.</summary>
public sealed record TableCopy(string Table, int Rows, int Truncated = 0);

/// <summary>
/// Copies every table of one Krakarr database into another, through EF Core so each provider's types (dates, arrays,
/// JSON columns) convert the same way the app reads and writes them. Used to switch between SQLite and PostgreSQL.
/// </summary>
/// <remarks>
/// Both databases must be migrated to the same schema, and the target must be empty (<see cref="IsEmptyAsync"/>).
/// Tables are copied parents first, in one transaction on the target, so a failure leaves it empty again. Rows are
/// streamed from the source and saved in batches with the change tracker cleared in between, so memory stays flat
/// whatever the library size. Ids are kept, and PostgreSQL identity sequences are moved past them.
/// </remarks>
public static class DatabaseCopier
{
    private const int BatchSize = 500;
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly MethodInfo CopyTableMethod =
        typeof(DatabaseCopier).GetMethod(nameof(CopyTableAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo AnyRowsMethod =
        typeof(DatabaseCopier).GetMethod(nameof(AnyRowsAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Entity types that own a table, parents before the tables that reference them.</summary>
    public static IReadOnlyList<IEntityType> TablesInCopyOrder(IModel model)
    {
        var tables = model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.FindPrimaryKey() is not null && t.GetTableName() is not null)
            .OrderBy(t => t.GetTableName(), StringComparer.Ordinal)
            .ToList();

        var ordered = new List<IEntityType>(tables.Count);
        var state = new Dictionary<IEntityType, bool>(); // false = visiting, true = done
        foreach (var table in tables) Visit(table);
        return ordered;

        void Visit(IEntityType type)
        {
            if (state.TryGetValue(type, out var done))
            {
                if (!done) throw new InvalidOperationException($"The foreign keys of {type.GetTableName()} form a cycle, so its rows can't be copied in order");
                return;
            }

            state[type] = false;
            foreach (var fk in type.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType.GetRootType();
                if (principal != type) Visit(principal);
            }

            state[type] = true;
            ordered.Add(type);
        }
    }

    /// <summary>True when the database holds no Krakarr rows (it may not exist yet, or only have the schema).</summary>
    public static async Task<bool> IsEmptyAsync(KrakarrDbContext db, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct)) return true;
        if (!(await db.Database.GetAppliedMigrationsAsync(ct)).Any()) return true;
        foreach (var table in TablesInCopyOrder(db.Model))
        {
            var any = (Task<bool>)AnyRowsMethod.MakeGenericMethod(table.ClrType).Invoke(null, [db, ct])!;
            if (await any) return false;
        }

        return true;
    }

    private static Task<bool> AnyRowsAsync<T>(KrakarrDbContext db, CancellationToken ct) where T : class => db.Set<T>().AnyAsync(ct);

    /// <summary>Copies every row from <paramref name="source"/> into the empty <paramref name="target"/>.</summary>
    /// <param name="progress">Called before each table with its name.</param>
    public static async Task<IReadOnlyList<TableCopy>> CopyAsync(
        KrakarrDbContext source, KrakarrDbContext target, Action<string>? progress = null, CancellationToken ct = default)
    {
        source.ChangeTracker.AutoDetectChangesEnabled = false;
        target.ChangeTracker.AutoDetectChangesEnabled = false;
        var toPostgres = target.Database.ProviderName == NpgsqlProviderName;

        var results = new List<TableCopy>();
        await using var transaction = await target.Database.BeginTransactionAsync(ct);
        foreach (var table in TablesInCopyOrder(target.Model))
        {
            progress?.Invoke(table.GetTableName()!);
            var copy = (Task<TableCopy>)CopyTableMethod.MakeGenericMethod(table.ClrType).Invoke(null, [source, target, toPostgres, ct])!;
            results.Add(await copy);
        }

        if (toPostgres) await ResetIdentitySequencesAsync(target, ct);
        await transaction.CommitAsync(ct);
        return results;
    }

    private static async Task<TableCopy> CopyTableAsync<T>(KrakarrDbContext source, KrakarrDbContext target, bool toPostgres, CancellationToken ct)
        where T : class
    {
        var entityType = target.Model.FindEntityType(typeof(T))!;
        var tableName = entityType.GetTableName()!;
        var textLimits = toPostgres
            ? entityType.GetProperties().Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is not null).ToArray()
            : [];
        var dates = toPostgres
            ? entityType.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)).ToArray()
            : [];

        int rows = 0, truncated = 0;
        await foreach (var row in source.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            var entry = target.Entry(row);
            // PostgreSQL rejects what SQLite lets through: offsets other than UTC, and text over a column's length.
            foreach (var property in dates)
            {
                var value = entry.Property(property).CurrentValue;
                if (value is DateTimeOffset date && date.Offset != TimeSpan.Zero) entry.Property(property).CurrentValue = date.ToUniversalTime();
            }

            foreach (var property in textLimits)
            {
                if (entry.Property(property).CurrentValue is string text && text.Length > property.GetMaxLength())
                {
                    entry.Property(property).CurrentValue = text[..property.GetMaxLength()!.Value];
                    truncated++;
                }
            }

            entry.State = EntityState.Added;
            if (++rows % BatchSize == 0) await SaveBatchAsync(target, ct);
        }

        await SaveBatchAsync(target, ct);

        var expected = await source.Set<T>().CountAsync(ct);
        var copied = await target.Set<T>().CountAsync(ct);
        if (copied != expected || rows != expected)
        {
            throw new InvalidOperationException($"{tableName}: the source has {expected} rows but {copied} were copied");
        }

        return new TableCopy(tableName, rows, truncated);
    }

    private static async Task SaveBatchAsync(KrakarrDbContext target, CancellationToken ct)
    {
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
    }

    /// <summary>Rows were inserted with their ids, so move each identity sequence past the highest one.</summary>
    private static async Task ResetIdentitySequencesAsync(KrakarrDbContext target, CancellationToken ct)
    {
        foreach (var table in TablesInCopyOrder(target.Model))
        {
            if (table.FindPrimaryKey()!.Properties is not [var key]
                || key.ValueGenerated != ValueGenerated.OnAdd
                || (key.ClrType != typeof(int) && key.ClrType != typeof(long)))
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table.GetTableName()!, table.GetSchema());
            var tableSql = table.GetSchema() is { } schema ? $"{Quote(schema)}.{Quote(store.Name)}" : Quote(store.Name);
            var column = key.GetColumnName(store)!;
            // pg_get_serial_sequence takes the table as a (quoted) identifier and the column as a plain name.
            var sql = $"SELECT setval(pg_get_serial_sequence('{tableSql.Replace("'", "''")}', '{column.Replace("'", "''")}'), "
                + $"COALESCE((SELECT MAX({Quote(column)}) FROM {tableSql}), 0) + 1, false)";
            await target.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
