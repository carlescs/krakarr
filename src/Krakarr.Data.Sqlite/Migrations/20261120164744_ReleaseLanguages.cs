using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261120164744_ReleaseLanguages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Languages",
            table: "Series",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Languages",
            table: "Movies",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DefaultLanguage",
            table: "Indexers",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Languages",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Languages",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "DefaultLanguage",
            table: "Indexers");
    }
}
