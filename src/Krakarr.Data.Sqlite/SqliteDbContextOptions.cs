using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Krakarr.Data.Sqlite;

public static class SqliteDbContextOptions
{
    public static DbContextOptionsBuilder UseKrakarrSqlite(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString, o => o.MigrationsAssembly(typeof(SqliteDbContextOptions).Assembly.GetName().Name));
}

/// <summary>Used by <c>dotnet ef</c> to generate Sqlite migrations.</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<KrakarrDbContext>
{
    public KrakarrDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<KrakarrDbContext>();
        builder.UseKrakarrSqlite("Data Source=design.db");
        return new KrakarrDbContext(builder.Options);
    }
}
