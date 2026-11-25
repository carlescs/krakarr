using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261125184236_SubtitleSyncAndUpgrade : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Manual",
            table: "SubtitleFiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SyncNote",
            table: "SubtitleFiles",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "SyncedAt",
            table: "SubtitleFiles",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Manual",
            table: "SubtitleFiles");

        migrationBuilder.DropColumn(
            name: "SyncNote",
            table: "SubtitleFiles");

        migrationBuilder.DropColumn(
            name: "SyncedAt",
            table: "SubtitleFiles");
    }
}
