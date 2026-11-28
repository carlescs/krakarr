using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261128080013_OmdbInfo : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Awards",
            table: "Series",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BoxOffice",
            table: "Series",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Certification",
            table: "Series",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Director",
            table: "Series",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ImdbRating",
            table: "Series",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ImdbVotes",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Metacritic",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OmdbPlot",
            table: "Series",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "OmdbUpdated",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RottenTomatoes",
            table: "Series",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RottenTomatoesUrl",
            table: "Series",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Writer",
            table: "Series",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Awards",
            table: "Movies",
            type: "TEXT",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BoxOffice",
            table: "Movies",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Certification",
            table: "Movies",
            type: "TEXT",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Director",
            table: "Movies",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ImdbRating",
            table: "Movies",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ImdbVotes",
            table: "Movies",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Metacritic",
            table: "Movies",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OmdbPlot",
            table: "Movies",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "OmdbUpdated",
            table: "Movies",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RottenTomatoes",
            table: "Movies",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RottenTomatoesUrl",
            table: "Movies",
            type: "TEXT",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Writer",
            table: "Movies",
            type: "TEXT",
            maxLength: 512,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Awards",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "BoxOffice",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Certification",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Director",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "ImdbRating",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "ImdbVotes",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Metacritic",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "OmdbPlot",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "OmdbUpdated",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "RottenTomatoes",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "RottenTomatoesUrl",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Writer",
            table: "Series");

        migrationBuilder.DropColumn(
            name: "Awards",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "BoxOffice",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "Certification",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "Director",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "ImdbRating",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "ImdbVotes",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "Metacritic",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "OmdbPlot",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "OmdbUpdated",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "RottenTomatoes",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "RottenTomatoesUrl",
            table: "Movies");

        migrationBuilder.DropColumn(
            name: "Writer",
            table: "Movies");
    }
}
