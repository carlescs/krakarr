using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261121084409_RequestResultUnseen : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MediaRequests_RequestedById",
            table: "MediaRequests");

        migrationBuilder.AddColumn<bool>(
            name: "ResultUnseen",
            table: "MediaRequests",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_RequestedById_ResultUnseen",
            table: "MediaRequests",
            columns: new[] { "RequestedById", "ResultUnseen" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MediaRequests_RequestedById_ResultUnseen",
            table: "MediaRequests");

        migrationBuilder.DropColumn(
            name: "ResultUnseen",
            table: "MediaRequests");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_RequestedById",
            table: "MediaRequests",
            column: "RequestedById");
    }
}
