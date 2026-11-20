using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261120061433_MovieReleaseDates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "DigitalRelease",
            table: "Movies",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<DateOnly>(
            name: "InCinemas",
            table: "Movies",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "MinimumAvailability",
            table: "Movies",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateOnly>(
            name: "PhysicalRelease",
            table: "Movies",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReleaseDates",
            table: "Movies",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_DigitalRelease",
            table: "Movies",
            column: "DigitalRelease");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_InCinemas",
            table: "Movies",
            column: "InCinemas");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_PhysicalRelease",
            table: "Movies",
            column: "PhysicalRelease");

        migrationBuilder.CreateIndex(
            name: "IX_Episodes_AirDate",
            table: "Episodes",
            column: "AirDate");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Movies_DigitalRelease",
            table: "Movies");

        migrationBuilder.DropIndex(
            name: "IX_Movies_InCinemas",
            table: "Movies");

        migrationBuilder.DropIndex(
            name: "IX_Movies_PhysicalRelease",
            table: "Movies");

        migrationBuilder.DropIndex(
            name: "IX_Episodes_AirDate",
            table: "Episodes");

        migrationBuilder.DropColumn(
            name: "DigitalRelease",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "InCinemas",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "MinimumAvailability",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "PhysicalRelease",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "ReleaseDates",
            table: "Movies");
    }
}
