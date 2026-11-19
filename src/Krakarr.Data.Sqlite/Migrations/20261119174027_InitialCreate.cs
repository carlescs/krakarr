using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Krakarr.Data.Sqlite.Migrations;

/// <inheritdoc />
public partial class _20261119174027_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Downloads",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                InfoHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: false),
                MagnetUri = table.Column<string>(type: "TEXT", nullable: true),
                TorrentFilePath = table.Column<string>(type: "TEXT", nullable: true),
                SavePath = table.Column<string>(type: "TEXT", nullable: false),
                State = table.Column<int>(type: "INTEGER", nullable: false),
                Paused = table.Column<bool>(type: "INTEGER", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                UploadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                DownloadedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Added = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                MediaType = table.Column<int>(type: "INTEGER", nullable: true),
                MovieId = table.Column<int>(type: "INTEGER", nullable: true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                QualityId = table.Column<int>(type: "INTEGER", nullable: true),
                IndexerName = table.Column<string>(type: "TEXT", nullable: true),
                Imported = table.Column<bool>(type: "INTEGER", nullable: false),
                ImportedAt = table.Column<long>(type: "INTEGER", nullable: true),
                ImportMessage = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Downloads", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "History",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                EventType = table.Column<int>(type: "INTEGER", nullable: false),
                Date = table.Column<long>(type: "INTEGER", nullable: false),
                SourceTitle = table.Column<string>(type: "TEXT", nullable: false),
                MovieId = table.Column<int>(type: "INTEGER", nullable: true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                QualityId = table.Column<int>(type: "INTEGER", nullable: true),
                Indexer = table.Column<string>(type: "TEXT", nullable: true),
                Message = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_History", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Indexers",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                ApiPath = table.Column<string>(type: "TEXT", nullable: false),
                ApiKey = table.Column<string>(type: "TEXT", nullable: true),
                MovieCategories = table.Column<string>(type: "TEXT", nullable: false),
                TvCategories = table.Column<string>(type: "TEXT", nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                EnableRss = table.Column<bool>(type: "INTEGER", nullable: false),
                Priority = table.Column<int>(type: "INTEGER", nullable: false),
                MinimumSeeders = table.Column<int>(type: "INTEGER", nullable: false),
                Added = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Indexers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MovieFiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                MovieId = table.Column<int>(type: "INTEGER", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                QualityId = table.Column<int>(type: "INTEGER", nullable: false),
                ReleaseGroup = table.Column<string>(type: "TEXT", nullable: true),
                SceneName = table.Column<string>(type: "TEXT", nullable: true),
                DateAdded = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MovieFiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "QualityProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                AllowedQualityIds = table.Column<string>(type: "TEXT", nullable: false),
                CutoffQualityId = table.Column<int>(type: "INTEGER", nullable: false),
                UpgradeAllowed = table.Column<bool>(type: "INTEGER", nullable: false),
                MinSizeMb = table.Column<long>(type: "INTEGER", nullable: false),
                MaxSizeMb = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_QualityProfiles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RootFolders",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Path = table.Column<string>(type: "TEXT", nullable: false),
                MediaType = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RootFolders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Series",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TvdbId = table.Column<int>(type: "INTEGER", nullable: false),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: false),
                Overview = table.Column<string>(type: "TEXT", nullable: true),
                PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                QualityProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Path = table.Column<string>(type: "TEXT", nullable: false),
                Added = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Series", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Settings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Settings", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Movies",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                OriginalTitle = table.Column<string>(type: "TEXT", nullable: true),
                SortTitle = table.Column<string>(type: "TEXT", nullable: false),
                Year = table.Column<int>(type: "INTEGER", nullable: false),
                Overview = table.Column<string>(type: "TEXT", nullable: true),
                PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                FanartUrl = table.Column<string>(type: "TEXT", nullable: true),
                Runtime = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                QualityProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                Path = table.Column<string>(type: "TEXT", nullable: false),
                Added = table.Column<long>(type: "INTEGER", nullable: false),
                LastSearched = table.Column<long>(type: "INTEGER", nullable: true),
                MovieFileId = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Movies", x => x.Id);
                table.ForeignKey(
                    name: "FK_Movies_MovieFiles_MovieFileId",
                    column: x => x.MovieFileId,
                    principalTable: "MovieFiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Movies_QualityProfiles_QualityProfileId",
                    column: x => x.QualityProfileId,
                    principalTable: "QualityProfiles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Episodes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                TvdbId = table.Column<int>(type: "INTEGER", nullable: false),
                SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: true),
                AirDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Episodes", x => x.Id);
                table.ForeignKey(
                    name: "FK_Episodes_Series_SeriesId",
                    column: x => x.SeriesId,
                    principalTable: "Series",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_InfoHash",
            table: "Downloads",
            column: "InfoHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_MovieId",
            table: "Downloads",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_Episodes_SeriesId_SeasonNumber_EpisodeNumber",
            table: "Episodes",
            columns: new[] { "SeriesId", "SeasonNumber", "EpisodeNumber" });

        migrationBuilder.CreateIndex(
            name: "IX_History_Date",
            table: "History",
            column: "Date");

        migrationBuilder.CreateIndex(
            name: "IX_History_MovieId",
            table: "History",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_Indexers_Name",
            table: "Indexers",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MovieFiles_MovieId",
            table: "MovieFiles",
            column: "MovieId");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_ImdbId",
            table: "Movies",
            column: "ImdbId");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_MovieFileId",
            table: "Movies",
            column: "MovieFileId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Movies_QualityProfileId",
            table: "Movies",
            column: "QualityProfileId");

        migrationBuilder.CreateIndex(
            name: "IX_Movies_TmdbId",
            table: "Movies",
            column: "TmdbId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RootFolders_Path",
            table: "RootFolders",
            column: "Path",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Series_TvdbId",
            table: "Series",
            column: "TvdbId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_Username",
            table: "Users",
            column: "Username",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Downloads");

        migrationBuilder.DropTable(
            name: "Episodes");

        migrationBuilder.DropTable(
            name: "History");

        migrationBuilder.DropTable(
            name: "Indexers");

        migrationBuilder.DropTable(
            name: "Movies");

        migrationBuilder.DropTable(
            name: "RootFolders");

        migrationBuilder.DropTable(
            name: "Settings");

        migrationBuilder.DropTable(
            name: "Users");

        migrationBuilder.DropTable(
            name: "Series");

        migrationBuilder.DropTable(
            name: "MovieFiles");

        migrationBuilder.DropTable(
            name: "QualityProfiles");
    }
}
