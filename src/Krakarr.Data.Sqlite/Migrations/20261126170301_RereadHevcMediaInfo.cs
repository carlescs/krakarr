using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261126170301_RereadHevcMediaInfo : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // HEVC files are read again on the next rescan: the bitstream may say HDR10+, or HDR the container didn't repeat.
        migrationBuilder.Sql("UPDATE \"MovieFiles\" SET \"MediaInfo\" = NULL WHERE \"MediaInfo\" LIKE '%\"vc\":\"HEVC\"%';");
        migrationBuilder.Sql("UPDATE \"EpisodeFiles\" SET \"MediaInfo\" = NULL WHERE \"MediaInfo\" LIKE '%\"vc\":\"HEVC\"%';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Nothing to undo: the files were read again.
    }
}
