using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261120164748_ReleaseLanguages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<List<string>>(
            name: "Languages",
            table: "Series",
            type: "text[]",
            nullable: true);

        migrationBuilder.AddColumn<List<string>>(
            name: "Languages",
            table: "Movies",
            type: "text[]",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DefaultLanguage",
            table: "Indexers",
            type: "text",
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
