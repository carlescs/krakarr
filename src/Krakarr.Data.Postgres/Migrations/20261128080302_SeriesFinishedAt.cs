using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261128080302_SeriesFinishedAt : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "FinishedAt",
            table: "Series",
            type: "timestamp with time zone",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FinishedAt",
            table: "Series");
    }
}
