using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261128140254_CustomFormats : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CutoffFormatScore",
            table: "QualityProfiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "FormatScores",
            table: "QualityProfiles",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<int>(
            name: "MinFormatScore",
            table: "QualityProfiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "CustomFormatIds",
            table: "MovieFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CustomFormatScore",
            table: "MovieFiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "CustomFormatIds",
            table: "EpisodeFiles",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CustomFormatScore",
            table: "EpisodeFiles",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "CustomFormats",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Specifications = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomFormats", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomFormats_Name",
            table: "CustomFormats",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CustomFormats");

        migrationBuilder.DropColumn(
            name: "CutoffFormatScore",
            table: "QualityProfiles");

        migrationBuilder.DropColumn(
            name: "FormatScores",
            table: "QualityProfiles");

        migrationBuilder.DropColumn(
            name: "MinFormatScore",
            table: "QualityProfiles");

        migrationBuilder.DropColumn(
            name: "CustomFormatIds",
            table: "MovieFiles");

        migrationBuilder.DropColumn(
            name: "CustomFormatScore",
            table: "MovieFiles");

        migrationBuilder.DropColumn(
            name: "CustomFormatIds",
            table: "EpisodeFiles");

        migrationBuilder.DropColumn(
            name: "CustomFormatScore",
            table: "EpisodeFiles");
    }
}
