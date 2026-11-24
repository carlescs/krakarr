using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Krakarr.Data.Sqlite;

/// <summary>
/// Replaces SQLite's built-in <c>lower()</c>/<c>upper()</c>, which only fold ASCII, with .NET's invariant casing, so
/// <c>ToLower()</c> in LINQ (case-insensitive search, username checks) also matches "À", "É", "Ö" and the like, as it
/// does on PostgreSQL.
/// </summary>
internal sealed class UnicodeCaseFunctions : DbConnectionInterceptor
{
    public static readonly UnicodeCaseFunctions Instance = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Register(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Register(connection);
        return Task.CompletedTask;
    }

    private static void Register(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        sqlite.CreateFunction<string?, string?>("lower", s => s?.ToLowerInvariant(), isDeterministic: true);
        sqlite.CreateFunction<string?, string?>("upper", s => s?.ToUpperInvariant(), isDeterministic: true);
    }
}
