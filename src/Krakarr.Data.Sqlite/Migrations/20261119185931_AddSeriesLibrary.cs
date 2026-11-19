using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261119185931_AddSeriesLibrary : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.RenameColumn(
            name: "RelativePath",
            table: "Episodes",
            newName: "Overview");

        migrationBuilder.AddColumn<string>(
            name: "FanartUrl",
            table: "Series",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "FirstAired",
            table: "Series",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastRefreshed",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "LastSearched",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Network",
            table: "Series",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Runtime",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "SeasonFolder",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SortTitle",
            table: "Series",
            type: "TEXT",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "Status",
            table: "Series",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "AbsoluteNumber",
            table: "Episodes",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "EpisodeFileId",
            table: "Episodes",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Runtime",
            table: "Episodes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "EpisodeIds",
            table: "Downloads",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateTable(
            name: "EpisodeFiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                QualityId = table.Column<int>(type: "INTEGER", nullable: false),
                ReleaseGroup = table.Column<string>(type: "TEXT", nullable: true),
                SceneName = table.Column<string>(type: "TEXT", nullable: true),
                DateAdded = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EpisodeFiles", x => x.Id);
                table.ForeignKey(
                    name: "FK_EpisodeFiles_Series_SeriesId",
                    column: x => x.SeriesId,
                    principalTable: "Series",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Seasons",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                Monitored = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Seasons", x => x.Id);
                table.ForeignKey(
                    name: "FK_Seasons_Series_SeriesId",
                    column: x => x.SeriesId,
                    principalTable: "Series",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Series_QualityProfileId",
            table: "Series",
            column: "QualityProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_Episodes_EpisodeFileId",
            table: "Episodes",
            column: "EpisodeFileId");

        migrationBuilder.CreateIndex(
            name: "IX_Episodes_TvdbId",
            table: "Episodes",
            column: "TvdbId");

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_SeriesId",
            table: "Downloads",
            column: "SeriesId");

        migrationBuilder.CreateIndex(
            name: "IX_EpisodeFiles_SeriesId",
            table: "EpisodeFiles",
            column: "SeriesId");

        migrationBuilder.CreateIndex(
            name: "IX_Seasons_SeriesId_SeasonNumber",
            table: "Seasons",
            columns: new[] { "SeriesId", "SeasonNumber" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_Episodes_EpisodeFiles_EpisodeFileId",
            table: "Episodes",
            column: "EpisodeFileId",
            principalTable: "EpisodeFiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_Series_QualityProfiles_QualityProfileId",
            table: "Series",
            column: "QualityProfileId",
            principalTable: "QualityProfiles",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Episodes_EpisodeFiles_EpisodeFileId",
            table: "Episodes");

        migrationBuilder.DropForeignKey(
            name: "FK_Series_QualityProfiles_QualityProfileId",
            table: "Series");

        migrationBuilder.DropTable(
            name: "EpisodeFiles");

        migrationBuilder.DropTable(
            name: "Seasons");

        migrationBuilder.DropIndex(
            name: "IX_Series_QualityProfileId",
            table: "Series");

        migrationBuilder.DropIndex(
            name: "IX_Episodes_EpisodeFileId",
            table: "Episodes");

        migrationBuilder.DropIndex(
            name: "IX_Episodes_TvdbId",
            table: "Episodes");

        migrationBuilder.DropIndex(
            name: "IX_Downloads_SeriesId",
            table: "Downloads");

        migrationBuilder.DropColumn(
            name: "FanartUrl",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "FirstAired",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "LastRefreshed",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "LastSearched",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Network",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Runtime",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "SeasonFolder",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "SortTitle",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Status",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "AbsoluteNumber",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "EpisodeFileId",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "Runtime",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "EpisodeIds",
            table: "Downloads");

        migrationBuilder.RenameColumn(
            name: "Overview",
            table: "Episodes",
            newName: "RelativePath");
    }
}
