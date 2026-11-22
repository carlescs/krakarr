using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261122084140_EmbeddedSubtitles : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "EmbeddedSubtitles",
            table: "MovieFiles",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EmbeddedSubtitles",
            table: "EpisodeFiles",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "EmbeddedSubtitles",
            table: "MovieFiles");

        migrationBuilder.DropColumn(
            name: "EmbeddedSubtitles",
            table: "EpisodeFiles");
    }
}
