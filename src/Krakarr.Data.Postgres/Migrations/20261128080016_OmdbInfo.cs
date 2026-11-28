using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Postgres.Migrations;

/// <inheritdoc />
public partial class _20261128080016_OmdbInfo : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Awards",
            table: "Series",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BoxOffice",
            table: "Series",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Certification",
            table: "Series",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Director",
            table: "Series",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ImdbRating",
            table: "Series",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ImdbVotes",
            table: "Series",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Metacritic",
            table: "Series",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OmdbPlot",
            table: "Series",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "OmdbUpdated",
            table: "Series",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RottenTomatoes",
            table: "Series",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RottenTomatoesUrl",
            table: "Series",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Writer",
            table: "Series",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Awards",
            table: "Movies",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "BoxOffice",
            table: "Movies",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Certification",
            table: "Movies",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Director",
            table: "Movies",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "ImdbRating",
            table: "Movies",
            type: "double precision",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ImdbVotes",
            table: "Movies",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Metacritic",
            table: "Movies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OmdbPlot",
            table: "Movies",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "OmdbUpdated",
            table: "Movies",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RottenTomatoes",
            table: "Movies",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RottenTomatoesUrl",
            table: "Movies",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Writer",
            table: "Movies",
            type: "character varying(512)",
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
