using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261128080254_SeriesFinishedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "FinishedAt",
            table: "Series",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FinishedAt",
            table: "Series");
    }
}
