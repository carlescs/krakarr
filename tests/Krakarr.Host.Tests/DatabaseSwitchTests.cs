using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Krakarr.Host.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Krakarr.Host.Tests;

public sealed class DatabaseCopyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("krakarr-db").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static DatabaseTarget SqliteAt(string file) => new(DatabaseTarget.Sqlite, $"Data Source={file}");

    /// <summary>A database with a row in every table, including the references between them.</summary>
    internal static async Task SeedAsync(DatabaseTarget target, CancellationToken ct)
    {
        await using var db = target.CreateContext();
        await db.Database.MigrateAsync(ct);

        var user = new User { Username = "admin", PasswordHash = "hash", Role = UserRole.Admin };
        var profile = new QualityProfile { Name = "HD", AllowedQualityIds = [Qualities.WebDl1080.Id, Qualities.Bluray1080.Id], CutoffQualityId = Qualities.Bluray1080.Id };
        var collection = new MovieCollection { Name = "Trilogy", TmdbId = 10 };
        db.AddRange(user, profile, collection);
        db.Settings.Add(new SettingEntry { Key = "general", Value = """{"apiKey":"abc"}""" });
        db.RootFolders.Add(new RootFolder { Path = "/movies" });
        db.Indexers.Add(new Indexer { Name = "Indexer", BaseUrl = "https://example.org" });
        await db.SaveChangesAsync(ct);

        db.UserPasskeys.Add(new UserPasskey { UserId = user.Id, CredentialId = [1, 2, 3], PublicKey = [4, 5, 6], Name = "Key" });
        var movie = new Movie
        {
            Title = "Movie", TmdbId = 100, Year = 2020, Path = "/movies/Movie (2020)", QualityProfileId = profile.Id, CollectionId = collection.Id,
            ReleaseDates = [new MovieReleaseDate("US", ReleaseDateType.Digital, new DateOnly(2020, 5, 1))],
            DigitalRelease = new DateOnly(2020, 5, 1), Languages = ["en", "es"],
            // SQLite keeps the offset; PostgreSQL only takes UTC.
            Added = new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.FromHours(2)),
        };
        var series = new Core.Domain.Series { Title = "Show", TmdbId = 200, Path = "/tv/Show", QualityProfileId = profile.Id };
        db.AddRange(movie, series);
        await db.SaveChangesAsync(ct);

        var movieFile = new MovieFile { MovieId = movie.Id, RelativePath = "Movie.mkv", Size = 1_000, QualityId = Qualities.WebDl1080.Id };
        var episodeFile = new EpisodeFile { SeriesId = series.Id, SeasonNumber = 1, RelativePath = "S01E01.mkv", Size = 500, QualityId = Qualities.WebDl1080.Id };
        db.AddRange(movieFile, episodeFile);
        db.Seasons.Add(new Season { SeriesId = series.Id, SeasonNumber = 1 });
        db.AlternateTitles.Add(new AlternateTitle { MovieId = movie.Id, Title = "La Película", NormalizedTitle = "la pelicula" });
        await db.SaveChangesAsync(ct);

        movie.MovieFileId = movieFile.Id;
        db.Episodes.Add(new Episode { SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 1, EpisodeFileId = episodeFile.Id, AirDate = new DateOnly(2021, 1, 1) });
        db.SubtitleFiles.Add(new SubtitleFile { MovieFileId = movieFile.Id, Language = "en", Path = "Movie.en.srt", Provider = "opensubtitles" });
        db.Downloads.Add(new Download { InfoHash = "abcdef", Name = "Movie.2020.1080p", SavePath = "/downloads", MovieId = movie.Id });
        db.History.Add(new HistoryEntry { SourceTitle = "Movie.2020.1080p", MovieId = movie.Id, EventType = HistoryEventType.Grabbed });
        db.MediaRequests.Add(new MediaRequest { MediaType = MediaType.Movie, TmdbId = 300, Title = "Wanted", RequestedById = user.Id });
        db.MediaSubscriptions.Add(new MediaSubscription { UserId = user.Id, MovieId = movie.Id });
        db.NotificationDismissals.Add(new NotificationDismissal { UserId = user.Id, NotificationId = "health:disk" });
        await db.SaveChangesAsync(ct);
    }

    internal static async Task AssertCopiedAsync(DatabaseTarget target, CancellationToken ct)
    {
        await using var db = target.CreateContext();
        var movie = await db.Movies.AsNoTracking().SingleAsync(ct);
        Assert.Equal("Movie", movie.Title);
        Assert.Equal(["en", "es"], movie.Languages);
        Assert.Equal(new DateOnly(2020, 5, 1), Assert.Single(movie.ReleaseDates).Date);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero), movie.Added.ToUniversalTime());
        Assert.NotNull(movie.MovieFileId);
        Assert.Equal(movie.MovieFileId, (await db.MovieFiles.SingleAsync(ct)).Id);
        Assert.Equal([Qualities.WebDl1080.Id, Qualities.Bluray1080.Id], (await db.QualityProfiles.SingleAsync(ct)).AllowedQualityIds);
        Assert.Equal([1, 2, 3], (await db.UserPasskeys.SingleAsync(ct)).CredentialId);
        Assert.Equal("La Película", (await db.AlternateTitles.SingleAsync(ct)).Title);
        Assert.NotNull((await db.Episodes.SingleAsync(ct)).EpisodeFileId);

        // Ids carry on after the copied ones.
        var added = new Movie { Title = "Next", TmdbId = 101, Path = "/movies/Next", QualityProfileId = movie.QualityProfileId };
        db.Movies.Add(added);
        await db.SaveChangesAsync(ct);
        Assert.True(added.Id > movie.Id);
    }

    [Fact]
    public async Task Copies_every_table_between_sqlite_databases()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = SqliteAt(Path.Combine(_dir, "source.db"));
        var target = SqliteAt(Path.Combine(_dir, "target.db"));
        await SeedAsync(source, ct);

        IReadOnlyList<TableCopy> tables;
        await using (var from = source.CreateContext())
        await using (var to = target.CreateContext())
        {
            await to.Database.MigrateAsync(ct);
            Assert.True(await DatabaseCopier.IsEmptyAsync(to, ct));
            tables = await DatabaseCopier.CopyAsync(from, to, ct: ct);
            Assert.False(await DatabaseCopier.IsEmptyAsync(to, ct));
        }

        Assert.Equal(DatabaseCopier.TablesInCopyOrder(source.CreateContext().Model).Count, tables.Count);
        Assert.All(tables, t => Assert.True(t.Rows > 0, $"{t.Table} has no rows"));
        await AssertCopiedAsync(target, ct);
    }

    [Fact]
    public void Copies_parents_before_the_tables_that_reference_them()
    {
        using var db = SqliteAt(Path.Combine(_dir, "order.db")).CreateContext();
        var order = DatabaseCopier.TablesInCopyOrder(db.Model).Select(t => t.GetTableName()).ToList();
        Assert.True(order.IndexOf("MovieFiles") < order.IndexOf("Movies"));
        Assert.True(order.IndexOf("QualityProfiles") < order.IndexOf("Movies"));
        Assert.True(order.IndexOf("Series") < order.IndexOf("EpisodeFiles"));
        Assert.True(order.IndexOf("EpisodeFiles") < order.IndexOf("Episodes"));
        Assert.True(order.IndexOf("Users") < order.IndexOf("MediaRequests"));
    }

    [Fact]
    public async Task Copies_sqlite_into_postgres_and_back()
    {
        // Needs a PostgreSQL server: KRAKARR_TEST_POSTGRES is a connection string for a user that may create databases.
        if (Environment.GetEnvironmentVariable("KRAKARR_TEST_POSTGRES") is not { Length: > 0 } server)
        {
            Assert.Skip("Set KRAKARR_TEST_POSTGRES to run against PostgreSQL");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var name = "krakarr_test_" + Guid.NewGuid().ToString("N")[..12];
        var postgres = new DatabaseTarget(DatabaseTarget.Postgres, new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString);
        var source = SqliteAt(Path.Combine(_dir, "source.db"));
        var back = SqliteAt(Path.Combine(_dir, "back.db"));
        await SeedAsync(source, ct);
        try
        {
            await using (var from = source.CreateContext())
            await using (var to = postgres.CreateContext())
            {
                await to.Database.MigrateAsync(ct);
                await DatabaseCopier.CopyAsync(from, to, ct: ct);
            }

            await AssertCopiedAsync(postgres, ct);

            await using (var from = postgres.CreateContext())
            await using (var to = back.CreateContext())
            {
                await to.Database.MigrateAsync(ct);
                await DatabaseCopier.CopyAsync(from, to, ct: ct);
            }

            await using var check = back.CreateContext();
            Assert.Equal(2, await check.Movies.CountAsync(ct));
        }
        finally
        {
            await using var cleanup = postgres.CreateContext();
            await cleanup.Database.EnsureDeletedAsync(CancellationToken.None);
        }
    }
}

public sealed class DatabaseSwitchStartupTests : IDisposable
{
    private readonly KrakarrPaths _paths;
    private readonly DatabaseTarget _source;
    private readonly DatabaseTarget _target;

    public DatabaseSwitchStartupTests()
    {
        _paths = new KrakarrPaths
        {
            ConfigDirectory = Directory.CreateTempSubdirectory("krakarr-switch").FullName,
            DefaultDownloadDirectory = Path.GetTempPath(),
        };
        _source = DatabaseCopyTests.SqliteAt(Path.Combine(_paths.ConfigDirectory, "krakarr.db"));
        _target = DatabaseCopyTests.SqliteAt(Path.Combine(_paths.ConfigDirectory, "new.db"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_paths.ConfigDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Stage() => DatabaseSwitch.WritePlan(_paths, new DatabaseSwitchPlan(_source, _target, DateTimeOffset.UtcNow));

    private Task<DatabaseSwitch.Result?> ApplyAsync(DatabaseTarget configured, params string[] pinned) =>
        DatabaseSwitch.ApplyPendingAsync(_paths, configured, pinned, NullLogger.Instance, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Does_nothing_without_a_staged_switch()
    {
        Assert.Null(await ApplyAsync(_source));
    }

    [Fact]
    public async Task Copies_the_data_and_points_config_json_at_the_new_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await DatabaseCopyTests.SeedAsync(_source, ct);
        File.WriteAllText(_paths.ConfigFile, """{ "Krakarr": { "Port": 9000 }, "database": { "provider": "sqlite" } }""");
        Stage();

        var result = await ApplyAsync(_source);

        Assert.True(result!.Outcome.Succeeded, result.Outcome.Message);
        Assert.Equal(_target, result.Use);
        Assert.False(File.Exists(_paths.PendingDatabaseSwitchFile));
        await DatabaseCopyTests.AssertCopiedAsync(_target, ct);

        var config = new ConfigurationBuilder().AddJsonFile(_paths.ConfigFile).Build();
        Assert.Equal("9000", config["Krakarr:Port"]);
        Assert.Equal("sqlite", config["Database:Provider"]);
        Assert.Equal(_target.ConnectionString, config["Database:ConnectionString"]);
        Assert.Single(JsonNode.Parse(File.ReadAllText(_paths.ConfigFile))!.AsObject(), p => p.Key.Equals("Database", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(_paths.ConfigFile + DatabaseSwitch.PreSwitchSuffix));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.ConfigFile));
        }

        // The old database is untouched.
        await using var old = _source.CreateContext();
        Assert.Equal(1, await old.Movies.CountAsync(ct));
    }

    [Fact]
    public async Task Refuses_a_database_that_already_holds_data_and_stays_on_the_old_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await DatabaseCopyTests.SeedAsync(_source, ct);
        await DatabaseCopyTests.SeedAsync(_target, ct);
        Stage();

        var result = await ApplyAsync(_source);

        Assert.False(result!.Outcome.Succeeded);
        Assert.Contains("already holds Krakarr data", result.Outcome.Message);
        Assert.Equal(_source, result.Use);
        Assert.False(File.Exists(_paths.PendingDatabaseSwitchFile));
        Assert.True(File.Exists(Path.Combine(_paths.ConfigDirectory, DatabaseSwitch.FailedFileName)));
        Assert.False(File.Exists(_paths.ConfigFile));
    }

    [Fact]
    public async Task Waits_while_environment_variables_choose_the_old_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await DatabaseCopyTests.SeedAsync(_source, ct);
        Stage();

        var result = await ApplyAsync(_source, "Database__Provider");

        Assert.False(result!.Outcome.Succeeded);
        Assert.Contains("Database__Provider", result.Outcome.Message);
        Assert.Equal(_source, result.Use);
        Assert.True(File.Exists(_paths.PendingDatabaseSwitchFile));
        Assert.False(File.Exists(_target.SqliteFile()));
    }

    [Fact]
    public async Task Copies_once_the_environment_points_at_the_new_database()
    {
        var ct = TestContext.Current.CancellationToken;
        await DatabaseCopyTests.SeedAsync(_source, ct);
        Stage();

        var result = await ApplyAsync(_target, "Database__Provider", "Database__ConnectionString");

        Assert.True(result!.Outcome.Succeeded, result.Outcome.Message);
        Assert.Equal(_target, result.Use);
        Assert.False(File.Exists(_paths.ConfigFile)); // the environment chooses the database, not config.json
        await DatabaseCopyTests.AssertCopiedAsync(_target, ct);
    }

    [Fact]
    public async Task Keeps_running_on_the_old_database_when_the_copy_fails_after_the_environment_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        await DatabaseCopyTests.SeedAsync(_source, ct);
        await DatabaseCopyTests.SeedAsync(_target, ct);
        Stage();

        var result = await ApplyAsync(_target, "Database__Provider");

        Assert.False(result!.Outcome.Succeeded);
        Assert.Equal(_source, result.Use);
        Assert.True(File.Exists(_paths.PendingDatabaseSwitchFile)); // tried again on the next start
    }

    [Fact]
    public async Task Gives_up_when_the_configuration_points_at_another_database()
    {
        Stage();
        var other = DatabaseCopyTests.SqliteAt(Path.Combine(_paths.ConfigDirectory, "other.db"));

        var result = await ApplyAsync(other);

        Assert.False(result!.Outcome.Succeeded);
        Assert.Equal(other, result.Use);
        Assert.False(File.Exists(_paths.PendingDatabaseSwitchFile));
    }

    [Fact]
    public void Reports_database_settings_that_environment_variables_fix()
    {
        File.WriteAllText(_paths.ConfigFile, """{ "Database": { "Provider": "sqlite" } }""");
        var prefix = "KRAKARR_TEST_" + Guid.NewGuid().ToString("N")[..8] + "_";
        Environment.SetEnvironmentVariable(prefix + "Database__ConnectionString", "Data Source=x.db");
        try
        {
            var before = new ConfigurationBuilder().AddEnvironmentVariables(prefix).AddJsonFile(_paths.ConfigFile).Build();
            Assert.Empty(DatabaseSwitch.PinnedSettings(before, _paths.ConfigFile));

            var after = new ConfigurationBuilder().AddJsonFile(_paths.ConfigFile).AddEnvironmentVariables(prefix).Build();
            Assert.Equal(["Database__ConnectionString"], DatabaseSwitch.PinnedSettings(after, _paths.ConfigFile));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Database__ConnectionString", null);
        }
    }

    [Fact]
    public void Compares_databases_without_the_password_and_describes_them_without_it()
    {
        var a = new DatabaseTarget(DatabaseTarget.Postgres, "Host=db;Database=krakarr;Username=krakarr;Password=secret");
        var b = new DatabaseTarget(DatabaseTarget.Postgres, "Host=DB;Port=5432;Database=krakarr;Username=krakarr;Password=changed");
        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(b with { ConnectionString = "Host=db;Database=other;Username=krakarr" }));
        Assert.Equal("krakarr@db:5432/krakarr", a.Describe());
        Assert.DoesNotContain("secret", a.ToString());
    }
}

public sealed class DatabaseEndpointTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task Tests_and_stages_a_switch_to_another_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        var info = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/database", ct);
        Assert.Equal("sqlite", info.GetProperty("provider").GetString());
        Assert.Equal(Path.Combine(factory.ConfigDirectory, "krakarr.db"), info.GetProperty("location").GetString());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("pending").ValueKind);

        var current = await client.PostAsJsonAsync("/api/v1/system/database/test", new { provider = "sqlite", file = "krakarr.db" }, ct);
        var currentTest = await current.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(currentTest.GetProperty("ok").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/system/database/test", new { provider = "sqlite", file = "../escape.db" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/system/database/test", new { provider = "postgres", host = "db" }, ct)).StatusCode);

        var test = await client.PostAsJsonAsync("/api/v1/system/database/test", new { provider = "sqlite", file = "moved.db" }, ct);
        var result = await test.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("empty").GetBoolean());
        Assert.False(File.Exists(Path.Combine(factory.ConfigDirectory, "moved.db"))); // testing creates nothing

        var staged = await client.PostAsJsonAsync("/api/v1/system/database/switch", new { provider = "sqlite", file = "moved.db" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, staged.StatusCode);
        info = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/database", ct);
        Assert.Equal(Path.Combine(factory.ConfigDirectory, "moved.db"), info.GetProperty("pending").GetProperty("location").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/system/database/switch", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/system/database/switch", ct)).StatusCode);
    }

    [Fact]
    public async Task Is_closed_to_anonymous_callers()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/system/database", ct)).StatusCode);
    }
}
