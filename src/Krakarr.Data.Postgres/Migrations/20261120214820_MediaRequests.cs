using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261120214820_MediaRequests : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaRequests",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MediaType = table.Column<int>(type: "integer", nullable: false),
                TmdbId = table.Column<int>(type: "integer", nullable: false),
                TvdbId = table.Column<int>(type: "integer", nullable: true),
                Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Year = table.Column<int>(type: "integer", nullable: false),
                PosterUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                RequestedById = table.Column<int>(type: "integer", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ProcessedById = table.Column<int>(type: "integer", nullable: true),
                ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeclineReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
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
