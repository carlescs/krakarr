using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261120162801_UserRoles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Role",
            table: "Users",
            type: "integer",
            nullable: false,
            defaultValue: 1); // Existing accounts (the setup administrator) become admins.

        migrationBuilder.AddColumn<string>(
            name: "SecurityStamp",
            table: "Users",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: ""); // Matches the stamp-less cookies issued before this migration.
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Role",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "SecurityStamp",
            table: "Users");
    }
}
