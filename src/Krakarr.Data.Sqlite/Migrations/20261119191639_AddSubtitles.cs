using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261119191639_AddSubtitles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "LastSubtitleSearch",
            table: "MovieFiles",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Language",
            table: "History",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastSubtitleSearch",
            table: "EpisodeFiles",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SubtitleFiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MovieFileId = table.Column<int>(type: "INTEGER", nullable: true),
                EpisodeFileId = table.Column<int>(type: "INTEGER", nullable: true),
                Language = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                HearingImpaired = table.Column<bool>(type: "INTEGER", nullable: false),
                Forced = table.Column<bool>(type: "INTEGER", nullable: false),
                Path = table.Column<string>(type: "TEXT", nullable: false),
                Provider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Score = table.Column<int>(type: "INTEGER", nullable: true),
                Release = table.Column<string>(type: "TEXT", nullable: true),
                DateAdded = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SubtitleFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_SubtitleFiles_EpisodeFiles_EpisodeFileId",
                    column: x => x.EpisodeFileId,
                    principalTable: "EpisodeFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_SubtitleFiles_MovieFiles_MovieFileId",
                    column: x => x.MovieFileId,
                    principalTable: "MovieFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SubtitleFiles_EpisodeFileId",
            table: "SubtitleFiles",
            column: "EpisodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_SubtitleFiles_MovieFileId",
            table: "SubtitleFiles",
            column: "MovieFileId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SubtitleFiles");

        migrationBuilder.DropColumn(
            name: "LastSubtitleSearch",
            table: "MovieFiles");

        migrationBuilder.DropColumn(
            name: "Language",
            table: "History");

        migrationBuilder.DropColumn(
            name: "LastSubtitleSearch",
            table: "EpisodeFiles");
    }
}
