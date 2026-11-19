using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Krakarr.Data.Postgres;

public static class PostgresDbContextOptions
{
    public static DbContextOptionsBuilder UseKrakarrPostgres(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, o => o.MigrationsAssembly(typeof(PostgresDbContextOptions).Assembly.GetName().Name));
}

/// <summary>Used by <c>dotnet ef</c> to generate Postgres migrations.</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<KrakarrDbContext>
{
    public KrakarrDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<KrakarrDbContext>();
        builder.UseKrakarrPostgres("Host=localhost;Database=krakarr;Username=krakarr;Password=krakarr");
        return new KrakarrDbContext(builder.Options);
    }
}
