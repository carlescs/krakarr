using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261124192026_EpisodeFileSubtitlesNotFound : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SubtitlesNotFound",
            table: "EpisodeFiles",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SubtitlesNotFound",
            table: "EpisodeFiles");
    }
}
