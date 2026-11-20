using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261120050428_SeriesMetadataSource : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Series",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Episodes",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AddColumn<int>(
            name: "TvdbId",
            table: "Episodes",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Episodes_TvdbId",
            table: "Episodes",
            column: "TvdbId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Episodes_TvdbId",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "TvdbId",
            table: "Episodes");

        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Series",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "TmdbId",
            table: "Episodes",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);
    }
}
