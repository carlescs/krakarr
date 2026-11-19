using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261119181538_AddDownloadClient : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Client",
            table: "Downloads",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "embedded");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Client",
            table: "Downloads");
    }
}
