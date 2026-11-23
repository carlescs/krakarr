using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261123150406_NotificationDismissals : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NotificationDismissals",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<int>(type: "integer", nullable: false),
                NotificationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Count = table.Column<int>(type: "integer", nullable: false),
                DismissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationDismissals", x => x.Id);
                table.ForeignKey(
                    name: "FK_NotificationDismissals_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationDismissals_UserId_NotificationId",
            table: "NotificationDismissals",
            columns: new[] { "UserId", "NotificationId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NotificationDismissals");
    }
}
