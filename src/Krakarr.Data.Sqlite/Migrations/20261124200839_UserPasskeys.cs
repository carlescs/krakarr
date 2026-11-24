using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261124200839_UserPasskeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserPasskeys",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                CredentialId = table.Column<byte[]>(type: "BLOB", maxLength: 1023, nullable: false),
                PublicKey = table.Column<byte[]>(type: "BLOB", maxLength: 2048, nullable: false),
                SignCount = table.Column<long>(type: "INTEGER", nullable: false),
                Transports = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserPasskeys", x => x.Id);
                table.ForeignKey(
                    name: "FK_UserPasskeys_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeys_CredentialId",
            table: "UserPasskeys",
            column: "CredentialId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_UserPasskeys_UserId",
            table: "UserPasskeys",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UserPasskeys");
    }
}
