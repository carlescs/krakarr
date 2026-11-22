using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261122083219_SeriesMetadataTitle : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MetadataTitle",
            table: "Series",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        // Existing series show their metadata title.
        migrationBuilder.Sql("UPDATE \"Series\" SET \"MetadataTitle\" = \"Title\"");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MetadataTitle",
            table: "Series");
    }
}
