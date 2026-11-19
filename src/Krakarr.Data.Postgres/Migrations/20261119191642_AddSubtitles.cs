using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261119191642_AddSubtitles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastSubtitleSearch",
            table: "MovieFiles",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Language",
            table: "History",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "LastSubtitleSearch",
            table: "EpisodeFiles",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "SubtitleFiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                MovieFileId = table.Column<int>(type: "integer", nullable: true),
                EpisodeFileId = table.Column<int>(type: "integer", nullable: true),
                Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                HearingImpaired = table.Column<bool>(type: "boolean", nullable: false),
                Forced = table.Column<bool>(type: "boolean", nullable: false),
                Path = table.Column<string>(type: "text", nullable: false),
                Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Score = table.Column<int>(type: "integer", nullable: true),
                Release = table.Column<string>(type: "text", nullable: true),
                DateAdded = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
