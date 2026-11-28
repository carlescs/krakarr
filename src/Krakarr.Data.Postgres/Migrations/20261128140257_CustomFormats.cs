using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261128140257_CustomFormats : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CutoffFormatScore",
            table: "QualityProfiles",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "FormatScores",
            table: "QualityProfiles",
            type: "text",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.AddColumn<int>(
            name: "MinFormatScore",
            table: "QualityProfiles",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<List<int>>(
            name: "CustomFormatIds",
            table: "MovieFiles",
            type: "integer[]",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CustomFormatScore",
            table: "MovieFiles",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<List<int>>(
            name: "CustomFormatIds",
            table: "EpisodeFiles",
            type: "integer[]",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "CustomFormatScore",
            table: "EpisodeFiles",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateTable(
            name: "CustomFormats",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Specifications = table.Column<string>(type: "text", nullable: false)
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
