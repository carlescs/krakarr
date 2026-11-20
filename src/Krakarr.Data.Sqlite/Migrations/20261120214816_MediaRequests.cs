using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261120214816_MediaRequests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaRequests",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                TvdbId = table.Column<int>(type: "INTEGER", nullable: true),
                Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: false),
                PosterUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                RequestedById = table.Column<int>(type: "INTEGER", nullable: false),
                RequestedAt = table.Column<long>(type: "INTEGER", nullable: false),
                ProcessedById = table.Column<int>(type: "INTEGER", nullable: true),
                ProcessedAt = table.Column<long>(type: "INTEGER", nullable: true),
                DeclineReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaRequests", x => x.Id);
                table.ForeignKey(
                    name: "FK_MediaRequests_Users_ProcessedById",
                    column: x => x.ProcessedById,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_MediaRequests_Users_RequestedById",
                    column: x => x.RequestedById,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_MediaType_TmdbId",
            table: "MediaRequests",
            columns: new[] { "MediaType", "TmdbId" });

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_ProcessedById",
            table: "MediaRequests",
            column: "ProcessedById");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_RequestedById",
            table: "MediaRequests",
            column: "RequestedById");

        migrationBuilder.CreateIndex(
            name: "IX_MediaRequests_Status_RequestedAt",
            table: "MediaRequests",
            columns: new[] { "Status", "RequestedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MediaRequests");
    }
}
