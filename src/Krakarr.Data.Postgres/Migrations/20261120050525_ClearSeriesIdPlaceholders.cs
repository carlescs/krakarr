using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261120050525_ClearSeriesIdPlaceholders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Placeholder ids from the switch to TMDB become "unknown"; the next refresh fills in the ids of the configured source.
        // A separate migration because SQLite runs table rebuilds after all other operations of a migration.
        migrationBuilder.Sql("UPDATE \"Series\" SET \"TmdbId\" = NULL WHERE \"TmdbId\" <= 0;");
        migrationBuilder.Sql("UPDATE \"Episodes\" SET \"TmdbId\" = NULL WHERE \"TmdbId\" <= 0;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE \"Series\" SET \"TmdbId\" = -\"Id\" WHERE \"TmdbId\" IS NULL;");
        migrationBuilder.Sql("UPDATE \"Episodes\" SET \"TmdbId\" = 0 WHERE \"TmdbId\" IS NULL;");
    }
}
