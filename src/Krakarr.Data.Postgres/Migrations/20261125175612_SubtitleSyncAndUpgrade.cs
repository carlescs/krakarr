using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261125175612_SubtitleSyncAndUpgrade : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Manual",
            table: "SubtitleFiles",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SyncNote",
            table: "SubtitleFiles",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "SyncedAt",
            table: "SubtitleFiles",
            type: "timestamp with time zone",
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
