using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261120045818_SeriesTmdbMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Series_TvdbId",
            table: "Series");

        migrationBuilder.RenameColumn(
            name: "TvdbId",
            table: "Episodes",
            newName: "TmdbId");

        migrationBuilder.RenameIndex(
            name: "IX_Episodes_TvdbId",
            table: "Episodes",
            newName: "IX_Episodes_TmdbId");

        // Episode ids came from TheTVDB; the next refresh fills in TMDB ids, matching episodes by season/number.
        migrationBuilder.Sql("UPDATE \"Episodes\" SET \"TmdbId\" = 0;");

        migrationBuilder.AlterColumn<int>(
            name: "TvdbId",
            table: "Series",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        // Series added with TheTVDB metadata get a unique placeholder; the next refresh resolves the real TMDB id from the TVDB id.
        migrationBuilder.Sql("UPDATE \"Series\" SET \"TmdbId\" = -\"Id\" WHERE \"TmdbId\" IS NULL;");

        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Series_TmdbId",
            table: "Series",
            column: "TmdbId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Series_TvdbId",
            table: "Series",
            column: "TvdbId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Series_TmdbId",
            table: "Series");

        migrationBuilder.DropIndex(
            name: "IX_Series_TvdbId",
            table: "Series");

        migrationBuilder.RenameColumn(
            name: "TmdbId",
            table: "Episodes",
            newName: "TvdbId");

        migrationBuilder.RenameIndex(
            name: "IX_Episodes_TmdbId",
            table: "Episodes",
            newName: "IX_Episodes_TvdbId");

        migrationBuilder.Sql("UPDATE \"Series\" SET \"TvdbId\" = -\"Id\" WHERE \"TvdbId\" IS NULL;");

        migrationBuilder.AlterColumn<int>(
            name: "TvdbId",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "INTEGER",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Series",
            type: "INTEGER",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "INTEGER");

        migrationBuilder.CreateIndex(
            name: "IX_Series_TvdbId",
            table: "Series",
            column: "TvdbId",
            unique: true);
    }
}
