using System.Collections;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Krakarr.Data;

/// <summary>Rows copied into one table, and how many of its text values had to be shortened to fit.</summary>
public sealed record TableCopy(string Table, int Rows, int Truncated = 0);

/// <summary>
/// Copies every table of one Krakarr database into another, through EF Core so each provider's types (dates, arrays,
/// JSON columns) convert the same way the app reads and writes them. Used to switch between SQLite and PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Everything is read from the EF model, so new entities need no change here: tables are found and ordered from
/// their foreign keys, and each row is copied property by property, shadow properties included. Join tables of
/// many-to-many relationships (shared-type entities), owned types (inline or in their own table), complex properties
/// and inheritance (TPH, TPT, TPC) are copied too. Foreign keys that form a cycle (a self-reference such as a parent
/// row, or two tables pointing at each other) are inserted empty and filled in once every row exists, as long as one
/// optional key breaks each cycle.
/// </para>
/// <para>
/// Both databases must be migrated to the same schema, and the target must be empty (<see cref="IsEmptyAsync"/>).
/// Tables are copied parents first, in one transaction on the target, so a failure leaves it empty again. Rows are
/// streamed from the source and saved in batches with both change trackers cleared in between, so memory stays flat
/// whatever the library size. Ids are kept, and PostgreSQL identity sequences are moved past them.
/// </para>
/// </remarks>
public static class DatabaseCopier
{
    private const int BatchSize = 500;
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly MethodInfo CopyTableMethod = Method(nameof(CopyTableAsync));
    private static readonly MethodInfo RestoreDeferredMethod = Method(nameof(RestoreDeferredKeysAsync));
    private static readonly MethodInfo AnyRowsMethod = Method(nameof(AnyRowsAsync));

    private static MethodInfo Method(string name) =>
        typeof(DatabaseCopier).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The tables to copy, parents first, and the foreign keys that must wait until every row exists.</summary>
    private sealed record CopyPlan(IReadOnlyList<IEntityType> Tables, IReadOnlyDictionary<IEntityType, IReadOnlyList<IForeignKey>> Deferred)
    {
        public IReadOnlyList<IForeignKey> DeferredFor(IEntityType table) => Deferred.TryGetValue(table, out var keys) ? keys : [];
    }

    /// <summary>Entity types that own a table, parents before the tables that reference them.</summary>
    /// <remarks>
    /// Owned types are copied with their owner and derived types with their base, so neither is listed. Foreign keys
    /// that close a cycle are left out of the ordering; <see cref="CopyAsync"/> fills them in afterwards.
    /// </remarks>
    public static IReadOnlyList<IEntityType> TablesInCopyOrder(IModel model) => Plan(model).Tables;

    private static CopyPlan Plan(IModel model)
    {
        var tables = model.GetEntityTypes()
            .Where(t => t.BaseType is null && !t.IsOwned() && t.FindPrimaryKey() is not null && MapsToTable(t))
            .OrderBy(TableName, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        var known = tables.ToHashSet();

        // Every reference from a table to another, including the ones declared on derived and owned types.
        var edges = tables.ToDictionary(t => t, t => ForeignKeysOf(t)
            .Select(fk => (Key: fk, Principal: TableOf(fk.PrincipalEntityType)))
            .Where(e => known.Contains(e.Principal))
            .ToList());

        // Keys inside a cycle can't all be inserted parents first; the optional ones are filled in afterwards.
        var component = StronglyConnectedComponents(tables, t => edges[t].Select(e => e.Principal));
        var deferred = new Dictionary<IEntityType, IReadOnlyList<IForeignKey>>();
        foreach (var table in tables)
        {
            var cyclic = edges[table]
                .Where(e => component[e.Principal] == component[table] && !e.Key.IsRequired && TableOf(e.Key.DeclaringEntityType) == table
                    && !e.Key.DeclaringEntityType.IsOwned())
                .Select(e => e.Key)
                .ToList();
            if (cyclic.Count > 0) deferred[table] = cyclic;
        }

        var ordered = new List<IEntityType>(tables.Count);
        var state = new Dictionary<IEntityType, bool>(); // false = visiting, true = done
        foreach (var table in tables) Visit(table);
        return new CopyPlan(ordered, deferred);

        void Visit(IEntityType type)
        {
            if (state.TryGetValue(type, out var done))
            {
                if (!done)
                {
                    throw new InvalidOperationException(
                        $"The required foreign keys of {TableName(type)} form a cycle, so its rows can't be copied in order");
                }

                return;
            }

            state[type] = false;
            var later = deferred.TryGetValue(type, out var keys) ? keys : [];
            foreach (var (key, principal) in edges[type])
            {
                if (!later.Contains(key)) Visit(principal);
            }

            state[type] = true;
            ordered.Add(type);
        }
    }

    private static bool MapsToTable(IEntityType type) =>
        type.GetDerivedTypesInclusive().Any(t => t.GetTableName() is not null);

    private static string TableName(IEntityType type) =>
        type.GetTableName() ?? type.GetDerivedTypes().Select(t => t.GetTableName()).FirstOrDefault(n => n is not null) ?? type.Name;

    /// <summary>The root entity type whose table rows of <paramref name="type"/> are copied with.</summary>
    private static IEntityType TableOf(IEntityType type)
    {
        while (type.IsOwned()) type = type.FindOwnership()!.PrincipalEntityType;
        return type.GetRootType();
    }

    /// <summary>The foreign keys of a table's type, its derived types and everything they own, but not ownerships.</summary>
    private static IEnumerable<IForeignKey> ForeignKeysOf(IEntityType table)
    {
        var types = new List<IEntityType>();
        var pending = new Stack<IEntityType>(table.GetDerivedTypesInclusive());
        while (pending.TryPop(out var type))
        {
            types.Add(type);
            foreach (var navigation in type.GetDeclaredNavigations())
            {
                if (navigation.ForeignKey.IsOwnership && !navigation.IsOnDependent) pending.Push(navigation.TargetEntityType);
            }
        }

        return types.SelectMany(t => t.GetDeclaredForeignKeys()).Where(fk => !fk.IsOwnership).Distinct();
    }

    /// <summary>Tarjan's algorithm: tables that reach each other through foreign keys share a component number.</summary>
    private static Dictionary<IEntityType, int> StronglyConnectedComponents(
        IReadOnlyList<IEntityType> nodes, Func<IEntityType, IEnumerable<IEntityType>> next)
    {
        var index = new Dictionary<IEntityType, int>();
        var low = new Dictionary<IEntityType, int>();
        var stack = new Stack<IEntityType>();
        var onStack = new HashSet<IEntityType>();
        var component = new Dictionary<IEntityType, int>();
        var counter = 0;
        foreach (var node in nodes)
        {
            if (!index.ContainsKey(node)) Connect(node);
        }

        return component;

        void Connect(IEntityType node)
        {
            index[node] = low[node] = counter++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var other in next(node))
            {
                if (!index.ContainsKey(other))
                {
                    Connect(other);
                    low[node] = Math.Min(low[node], low[other]);
                }
                else if (onStack.Contains(other))
                {
                    low[node] = Math.Min(low[node], index[other]);
                }
            }

            if (low[node] != index[node]) return;
            IEntityType member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component[member] = index[node];
            } while (member != node);
        }
    }

    /// <summary>True when the database holds no Krakarr rows (it may not exist yet, or only have the schema).</summary>
    public static async Task<bool> IsEmptyAsync(DbContext db, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct)) return true;
        if (!(await db.Database.GetAppliedMigrationsAsync(ct)).Any() && db.Database.GetMigrations().Any()) return true;
        foreach (var table in TablesInCopyOrder(db.Model))
        {
            var any = (Task<bool>)AnyRowsMethod.MakeGenericMethod(table.ClrType).Invoke(null, [db, table, ct])!;
            if (await any) return false;
        }

        return true;
    }

    private static Task<bool> AnyRowsAsync<T>(DbContext db, IEntityType table, CancellationToken ct) where T : class =>
        Rows<T>(db, table).AnyAsync(ct);

    /// <summary>The rows of a table; join tables and other shared-type entities are reached by name.</summary>
    private static IQueryable<T> Rows<T>(DbContext db, IEntityType table) where T : class =>
        table.HasSharedClrType ? db.Set<T>(table.Name) : db.Set<T>();

    private static DbSet<T> Set<T>(DbContext db, IEntityType table) where T : class =>
        table.HasSharedClrType ? db.Set<T>(table.Name) : db.Set<T>();

    /// <summary>Copies every row from <paramref name="source"/> into the empty <paramref name="target"/>.</summary>
    /// <param name="progress">Called before each table with its name.</param>
    public static async Task<IReadOnlyList<TableCopy>> CopyAsync(
        DbContext source, DbContext target, Action<string>? progress = null, CancellationToken ct = default)
    {
        source.ChangeTracker.AutoDetectChangesEnabled = false;
        target.ChangeTracker.AutoDetectChangesEnabled = false;
        var plan = Plan(target.Model);
        var fixes = new RowFixes(target.Database.ProviderName == NpgsqlProviderName);

        var results = new List<TableCopy>();
        await using var transaction = await target.Database.BeginTransactionAsync(ct);
        foreach (var table in plan.Tables)
        {
            progress?.Invoke(TableName(table));
            var sourceTable = SourceTable(source, table);
            var copy = (Task<TableCopy>)CopyTableMethod.MakeGenericMethod(table.ClrType)
                .Invoke(null, [source, target, sourceTable, table, plan.DeferredFor(table), fixes, ct])!;
            results.Add(await copy);
        }

        foreach (var (table, keys) in plan.Deferred)
        {
            var restore = (Task)RestoreDeferredMethod.MakeGenericMethod(table.ClrType)
                .Invoke(null, [source, target, SourceTable(source, table), table, keys, ct])!;
            await restore;
        }

        if (fixes.ToPostgres) await ResetIdentitySequencesAsync(target, ct);
        await transaction.CommitAsync(ct);
        return results;
    }

    private static IEntityType SourceTable(DbContext source, IEntityType table) =>
        source.Model.FindEntityType(table.Name)
        ?? throw new InvalidOperationException($"The source database has no {TableName(table)} table; migrate it first");

    private static async Task<TableCopy> CopyTableAsync<T>(
        DbContext source, DbContext target, IEntityType sourceTable, IEntityType table, IReadOnlyList<IForeignKey> deferred,
        RowFixes fixes, CancellationToken ct)
        where T : class
    {
        var tableName = TableName(table);
        var set = Set<T>(target, table);
        var deferredProperties = deferred.SelectMany(fk => fk.Properties).ToHashSet();
        var pairs = new List<(EntityEntry Source, object Copy)>();

        int rows = 0, truncated = 0;
        // Tracked, so shadow properties and owned rows can be read from the source's entries; cleared every batch.
        await foreach (var row in Rows<T>(source, sourceTable).AsTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            pairs.Clear();
            var copy = (T)CopyGraph(source, source.Entry(row), pairs);
            set.Add(copy);
            foreach (var (from, to) in pairs)
            {
                var entry = target.Entry(to);
                CopyValues(from.CurrentValues, entry.CurrentValues);
                foreach (var property in entry.Metadata.GetProperties())
                {
                    if (deferredProperties.Contains(property)) entry.Property(property).CurrentValue = null;
                }

                truncated += fixes.Apply(entry);
            }

            if (++rows % BatchSize == 0) await SaveBatchAsync(source, target, ct);
        }

        await SaveBatchAsync(source, target, ct);

        var expected = await Rows<T>(source, sourceTable).CountAsync(ct);
        var copied = await Rows<T>(target, table).CountAsync(ct);
        if (copied != expected || rows != expected)
        {
            throw new InvalidOperationException($"{tableName}: the source has {expected} rows but {copied} were copied");
        }

        return new TableCopy(tableName, rows, truncated);
    }

    /// <summary>
    /// A new instance holding the values of <paramref name="entry"/> and, recursively, copies of the entities it owns.
    /// Every source entry and its copy are added to <paramref name="pairs"/> so the caller can copy shadow values once
    /// the graph is tracked by the target.
    /// </summary>
    private static object CopyGraph(DbContext source, EntityEntry entry, List<(EntityEntry, object)> pairs)
    {
        var copy = entry.CurrentValues.ToObject();
        pairs.Add((entry, copy));
        foreach (var navigation in entry.Metadata.GetNavigations())
        {
            if (!navigation.ForeignKey.IsOwnership || navigation.IsOnDependent) continue;
            var value = navigation.GetGetter().GetClrValueUsingContainingEntity(entry.Entity);
            if (value is null) continue;

            if (navigation.IsCollection)
            {
                var accessor = navigation.GetCollectionAccessor()!;
                foreach (var item in (IEnumerable)value)
                {
                    accessor.Add(copy, CopyGraph(source, source.Entry(item), pairs), forMaterialization: true);
                }
            }
            else
            {
                var owned = CopyGraph(source, source.Entry(value), pairs);
                switch (navigation.GetMemberInfo(forMaterialization: true, forSet: true))
                {
                    case PropertyInfo property: property.SetValue(copy, owned); break;
                    case FieldInfo field: field.SetValue(copy, owned); break;
                }
            }
        }

        return copy;
    }

    /// <summary>
    /// Copies every value of an entity, shadow properties included, matching properties by name: the source and
    /// target use different models, so <see cref="PropertyValues.SetValues(PropertyValues)"/> doesn't accept them.
    /// Complex properties have no shadow values, so the copy made by <see cref="PropertyValues.ToObject"/> holds them.
    /// </summary>
    private static void CopyValues(PropertyValues from, PropertyValues to)
    {
        foreach (var property in to.Properties)
        {
            if (property.DeclaringType is not IEntityType) continue;
            foreach (var match in from.Properties)
            {
                if (match.DeclaringType is IEntityType && match.Name == property.Name)
                {
                    to[property] = from[match];
                    break;
                }
            }
        }
    }

    /// <summary>Fills in the foreign keys that were inserted empty because they close a cycle.</summary>
    private static async Task RestoreDeferredKeysAsync<T>(
        DbContext source, DbContext target, IEntityType sourceTable, IEntityType table, IReadOnlyList<IForeignKey> deferred, CancellationToken ct)
        where T : class
    {
        var set = Set<T>(target, table);
        var keys = deferred.SelectMany(fk => fk.Properties).ToHashSet();
        var rows = 0;
        await foreach (var row in Rows<T>(source, sourceTable).AsTracking().AsAsyncEnumerable().WithCancellation(ct))
        {
            var from = source.Entry(row);
            if (!keys.Any(k => from.Metadata.FindProperty(k.Name) is { } p && from.CurrentValues[p] is not null)) continue;

            // Attach a copy as the row now stands in the target, then change only the deferred keys.
            var entry = set.Attach((T)from.CurrentValues.ToObject());
            CopyValues(from.CurrentValues, entry.OriginalValues);
            CopyValues(from.CurrentValues, entry.CurrentValues);
            foreach (var property in entry.Metadata.GetProperties())
            {
                var isDeferred = keys.Contains(property);
                if (isDeferred) entry.Property(property).OriginalValue = null;
                if (!property.IsPrimaryKey()) entry.Property(property).IsModified = isDeferred;
            }

            if (++rows % BatchSize == 0) await SaveBatchAsync(source, target, ct);
        }

        await SaveBatchAsync(source, target, ct);
    }

    private static async Task SaveBatchAsync(DbContext source, DbContext target, CancellationToken ct)
    {
        await target.SaveChangesAsync(ct);
        target.ChangeTracker.Clear();
        source.ChangeTracker.Clear();
    }

    /// <summary>PostgreSQL rejects what SQLite lets through: offsets other than UTC, and text over a column's length.</summary>
    private sealed class RowFixes(bool toPostgres)
    {
        private readonly Dictionary<IEntityType, (IProperty[] Dates, IProperty[] Texts)> _properties = [];

        public bool ToPostgres { get; } = toPostgres;

        /// <summary>Fixes the values of one tracked row and returns how many texts were shortened.</summary>
        public int Apply(EntityEntry entry)
        {
            if (!ToPostgres) return 0;
            if (!_properties.TryGetValue(entry.Metadata, out var properties))
            {
                var all = entry.Metadata.GetProperties().ToArray();
                properties = (
                    all.Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)).ToArray(),
                    all.Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is not null).ToArray());
                _properties[entry.Metadata] = properties;
            }

            foreach (var property in properties.Dates)
            {
                var value = entry.Property(property);
                if (value.CurrentValue is DateTimeOffset date && date.Offset != TimeSpan.Zero) value.CurrentValue = date.ToUniversalTime();
            }

            var truncated = 0;
            foreach (var property in properties.Texts)
            {
                var value = entry.Property(property);
                if (value.CurrentValue is string text && text.Length > property.GetMaxLength())
                {
                    value.CurrentValue = text[..property.GetMaxLength()!.Value];
                    truncated++;
                }
            }

            return truncated;
        }
    }

    /// <summary>Rows were inserted with their ids, so move each identity sequence past the highest one.</summary>
    private static async Task ResetIdentitySequencesAsync(DbContext target, CancellationToken ct)
    {
        foreach (var type in target.Model.GetEntityTypes())
        {
            if (type.GetTableName() is not { } tableName || type.FindPrimaryKey() is not { } primaryKey) continue;
            var store = StoreObjectIdentifier.Table(tableName, type.GetSchema());
            var tableSql = type.GetSchema() is { } schema ? $"{Quote(schema)}.{Quote(tableName)}" : Quote(tableName);
            foreach (var key in primaryKey.Properties)
            {
                if (key.ValueGenerated != ValueGenerated.OnAdd || key.IsForeignKey()
                    || (key.ClrType != typeof(int) && key.ClrType != typeof(long))
                    || key.GetColumnName(store) is not { } column)
                {
                    continue;
                }

                // pg_get_serial_sequence takes the table as a (quoted) identifier and the column as a plain name; it's
                // NULL for a column without a sequence, and setval(NULL, ...) does nothing.
                var sql = $"SELECT setval(pg_get_serial_sequence('{tableSql.Replace("'", "''")}', '{column.Replace("'", "''")}'), "
                    + $"COALESCE((SELECT MAX({Quote(column)}) FROM {tableSql}), 0) + 1, false)";
                await target.Database.ExecuteSqlRawAsync(sql, ct);
            }
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
