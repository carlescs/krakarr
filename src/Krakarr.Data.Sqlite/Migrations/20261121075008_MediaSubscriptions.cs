using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261121075008_MediaSubscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MediaSubscriptions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                UserId = table.Column<int>(type: "INTEGER", nullable: false),
                MovieId = table.Column<int>(type: "INTEGER", nullable: true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MediaSubscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_MediaSubscriptions_Movies_MovieId",
                    column: x => x.MovieId,
                    principalTable: "Movies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MediaSubscriptions_Series_SeriesId",
                    column: x => x.SeriesId,
                    principalTable: "Series",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MediaSubscriptions_Users_UserId",
                    column: x => x.UserId,
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MediaSubscriptions_MovieId",
            table: "MediaSubscriptions",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaSubscriptions_SeriesId",
            table: "MediaSubscriptions",
            column: "SeriesId");

        migrationBuilder.CreateIndex(
            name: "IX_MediaSubscriptions_UserId_MovieId",
            table: "MediaSubscriptions",
            columns: new[] { "UserId", "MovieId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MediaSubscriptions_UserId_SeriesId",
            table: "MediaSubscriptions",
            columns: new[] { "UserId", "SeriesId" },
            unique: true);

        // Requesters follow the titles of their approved requests that are in the library.
        migrationBuilder.Sql("""
            INSERT INTO "MediaSubscriptions" ("UserId", "MovieId", "CreatedAt")
            SELECT r."RequestedById", m."Id", MIN(COALESCE(r."ProcessedAt", r."RequestedAt"))
            FROM "MediaRequests" r JOIN "Movies" m ON m."TmdbId" = r."TmdbId"
            WHERE r."MediaType" = 1 AND r."Status" = 1
            GROUP BY r."RequestedById", m."Id";
            """);
        migrationBuilder.Sql("""
            INSERT INTO "MediaSubscriptions" ("UserId", "SeriesId", "CreatedAt")
            SELECT r."RequestedById", s."Id", MIN(COALESCE(r."ProcessedAt", r."RequestedAt"))
            FROM "MediaRequests" r JOIN "Series" s ON s."TmdbId" = r."TmdbId" OR (r."TvdbId" IS NOT NULL AND s."TvdbId" = r."TvdbId")
            WHERE r."MediaType" = 2 AND r."Status" = 1
            GROUP BY r."RequestedById", s."Id";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MediaSubscriptions");
    }
}
