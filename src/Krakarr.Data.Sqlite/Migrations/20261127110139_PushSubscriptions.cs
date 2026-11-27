using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261127110139_PushSubscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PushSubscriptions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                P256dh = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Auth = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Topics = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PushSubscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_PushSubscriptions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_PushSubscriptions_Endpoint",
            table: "PushSubscriptions",
            column: "Endpoint",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PushSubscriptions_UserId",
            table: "PushSubscriptions",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PushSubscriptions");
    }
}
