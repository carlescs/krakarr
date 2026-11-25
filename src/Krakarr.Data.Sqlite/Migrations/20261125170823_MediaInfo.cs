using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261125170823_MediaInfo : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MediaInfo",
            table: "MovieFiles",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MediaInfo",
            table: "EpisodeFiles",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MediaInfo",
            table: "MovieFiles");

        migrationBuilder.DropColumn(
            name: "MediaInfo",
            table: "EpisodeFiles");
    }
}
