using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261124200842_UserPasskeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserPasskeys",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<int>(type: "integer", nullable: false),
                CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1023, nullable: false),
                PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 2048, nullable: false),
                SignCount = table.Column<long>(type: "bigint", nullable: false),
                Transports = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
