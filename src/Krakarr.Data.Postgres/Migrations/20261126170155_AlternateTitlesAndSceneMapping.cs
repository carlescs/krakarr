using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261126170155_AlternateTitlesAndSceneMapping : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SceneEpisodeNumber",
            table: "Episodes",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "SceneSeasonNumber",
            table: "Episodes",
            type: "integer",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "AlternateTitles",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MovieId = table.Column<int>(type: "integer", nullable: true),
                SeriesId = table.Column<int>(type: "integer", nullable: true),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                NormalizedTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                Source = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AlternateTitles", x => x.Id);
                table.ForeignKey(
                    name: "FK_AlternateTitles_Movies_MovieId",
                    column: x => x.MovieId,
                    principalTable: "Movies",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AlternateTitles_Series_SeriesId",
                    column: x => x.SeriesId,
                    principalTable: "Series",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AlternateTitles_MovieId",
            table: "AlternateTitles",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_AlternateTitles_NormalizedTitle",
            table: "AlternateTitles",
            column: "NormalizedTitle");

        migrationBuilder.CreateIndex(
            name: "IX_AlternateTitles_SeriesId",
            table: "AlternateTitles",
            column: "SeriesId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AlternateTitles");

        migrationBuilder.DropColumn(
            name: "SceneEpisodeNumber",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "SceneSeasonNumber",
            table: "Episodes");
    }
}
