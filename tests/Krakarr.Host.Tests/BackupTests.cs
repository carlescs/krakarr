using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Krakarr.Host.Backups;
using Microsoft.Data.Sqlite;

namespace Krakarr.Host.Tests;

public sealed class BackupEndpointTests(KrakarrFactory factory) : IClassFixture<KrakarrFactory>
{
    private async Task<HttpClient> AdminAsync(CancellationToken ct)
    {
        var client = factory.CreateClient(new() { HandleCookies = true });
        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/status", ct);
        var url = status.GetProperty("setupRequired").GetBoolean() ? "/api/v1/auth/setup" : "/api/v1/auth/login";
        (await client.PostAsJsonAsync(url, new { username = "admin", password = "correct-horse", setupToken = KrakarrFactory.TestSetupToken }, ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<string> BackUpAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.PostAsync("/api/v1/system/backups", null, ct);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("Manual", created.GetProperty("type").GetString());
        return created.GetProperty("name").GetString()!;
    }

    [Fact]
    public async Task Creates_a_backup_with_the_database_keys_and_manifest()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        var name = await BackUpAsync(client, ct);
        Assert.Matches(@"^krakarr_backup_\d{8}_\d{6}_manual(_\d+)?\.zip$", name);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/backups", ct);
        Assert.Contains(list.GetProperty("backups").EnumerateArray(), b => b.GetProperty("name").GetString() == name);
        Assert.False(list.GetProperty("restorePending").GetBoolean());
        Assert.Equal("sqlite", list.GetProperty("provider").GetString());

        var download = await client.GetAsync($"/api/v1/system/backups/{name}", ct);
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        await using var stream = await download.Content.ReadAsStreamAsync(ct);
        await using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var (manifest, error) = BackupArchive.Validate(zip, "sqlite");
        Assert.Null(error);
        Assert.Equal("Manual", manifest!.Type);
        Assert.NotNull(zip.GetEntry(BackupArchive.DatabaseEntry));
        Assert.Contains(zip.Entries, e => BackupArchive.IsKeyEntry(e.FullName));

        // Nothing is left behind next to the backups (the database copy, partial archives).
        var backupsDir = Path.Combine(factory.ConfigDirectory, "Backups");
        Assert.All(Directory.GetFiles(backupsDir), f => Assert.StartsWith("krakarr_backup_", Path.GetFileName(f)));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/system/backups/..%2Fkrakarr.db", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/system/backups/krakarr_backup_20000101_000000_manual.zip", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/system/backups/{name}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/system/backups/{name}", ct)).StatusCode);
    }

    [Fact]
    public async Task Keeps_only_the_configured_number_of_backups()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);
        (await client.PutAsJsonAsync("/api/v1/settings/backup", new { intervalDays = 7, retentionCount = 2 }, ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/v1/settings/backup", new { intervalDays = 7, retentionCount = 0 }, ct)).StatusCode);

        var names = new List<string>();
        for (var i = 0; i < 3; i++) names.Add(await BackUpAsync(client, ct));

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/backups", ct);
        var kept = list.GetProperty("backups").EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToList();
        Assert.Equal([names[2], names[1]], kept);
    }

    [Fact]
    public async Task Stages_a_valid_backup_for_restore_and_rejects_anything_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = await AdminAsync(ct);

        var junk = new ByteArrayContent("not a zip"u8.ToArray());
        junk.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var rejected = await client.PostAsync("/api/v1/system/backups/restore", junk, ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("zip", await rejected.Content.ReadAsStringAsync(ct));

        var name = await BackUpAsync(client, ct);
        var bytes = await client.GetByteArrayAsync($"/api/v1/system/backups/{name}", ct);
        var upload = new ByteArrayContent(bytes);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/v1/system/backups/restore", upload, ct)).StatusCode);
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/api/v1/system/backups", ct)).GetProperty("restorePending").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/system/backups/restore", ct)).StatusCode);
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/system/backups", ct)).GetProperty("restorePending").GetBoolean());

        // An existing backup can be restored too.
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/system/backups/{name}/restore", null, ct)).StatusCode);
        Assert.True(File.Exists(Path.Combine(factory.ConfigDirectory, "restore-pending.zip")));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/system/backups/restore", ct)).StatusCode);
    }
}

public sealed class BackupRestoreTests : IDisposable
{
    private readonly KrakarrPaths _paths;
    private readonly string _dbPath;

    public BackupRestoreTests()
    {
        var dir = Directory.CreateTempSubdirectory("krakarr-restore").FullName;
        _paths = new KrakarrPaths { ConfigDirectory = dir, DefaultDownloadDirectory = Path.Combine(dir, "downloads") };
        _paths.EnsureCreated();
        _dbPath = Path.Combine(dir, "krakarr.db");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_paths.ConfigDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Connection(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private static void CreateDatabase(string path, string value)
    {
        using var connection = new SqliteConnection(Connection(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Marker (Value TEXT); INSERT INTO Marker VALUES ($v);";
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = new SqliteConnection(Connection(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Marker";
        return (string)command.ExecuteScalar()!;
    }

    /// <summary>Writes a backup zip as the pending restore.</summary>
    private void StageBackup(string provider = "sqlite", Action<ZipArchive>? extra = null, byte[]? database = null)
    {
        var source = Path.Combine(_paths.ConfigDirectory, "source.db");
        File.Delete(source);
        CreateDatabase(source, "restored");
        using var zip = ZipFile.Open(_paths.PendingRestoreFile, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry(BackupArchive.ManifestEntry).Open()))
        {
            writer.Write(JsonSerializer.Serialize(new BackupManifest(1, "1.0.0", provider, DateTimeOffset.UtcNow, "Manual"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        if (database is null) zip.CreateEntryFromFile(source, BackupArchive.DatabaseEntry);
        else using (var s = zip.CreateEntry(BackupArchive.DatabaseEntry).Open()) s.Write(database);
        using (var writer = new StreamWriter(zip.CreateEntry("keys/key-restored.xml").Open())) writer.Write("<key id=\"restored\" />");
        extra?.Invoke(zip);
        File.Delete(source);
    }

    private RestoreOutcome? Apply(string provider = "sqlite") => BackupRestore.ApplyPending(_paths, provider, () => _dbPath);

    [Fact]
    public void Does_nothing_without_a_pending_restore()
    {
        CreateDatabase(_dbPath, "current");
        Assert.Null(Apply());
        Assert.Equal("current", ReadMarker(_dbPath));
    }

    [Fact]
    public void Restores_the_database_and_keys_and_keeps_the_previous_ones()
    {
        CreateDatabase(_dbPath, "current");
        File.WriteAllText(_dbPath + "-shm", "stale");
        Directory.CreateDirectory(_paths.KeysDirectory);
        File.WriteAllText(Path.Combine(_paths.KeysDirectory, "key-current.xml"), "<key id=\"current\" />");
        StageBackup();

        var outcome = Apply();

        Assert.NotNull(outcome);
        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal("restored", ReadMarker(_dbPath));
        Assert.Equal("current", ReadMarker(_dbPath + BackupRestore.PreRestoreSuffix));
        Assert.False(File.Exists(_dbPath + "-shm"));
        Assert.Equal(["key-restored.xml"], Directory.GetFiles(_paths.KeysDirectory).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(_paths.KeysDirectory + BackupRestore.PreRestoreSuffix, "key-current.xml")));
        Assert.False(File.Exists(_paths.PendingRestoreFile));
    }

    [Fact]
    public void Restores_config_json_and_reports_it()
    {
        CreateDatabase(_dbPath, "current");
        File.WriteAllText(_paths.ConfigFile, "{\"Krakarr\":{\"Port\":1}}");
        StageBackup(extra: zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry(BackupArchive.ConfigEntry).Open());
            writer.Write("{\"Krakarr\":{\"Port\":2}}");
        });
        var reloaded = false;

        var outcome = BackupRestore.ApplyPending(_paths, "sqlite", () => _dbPath, () => reloaded = true);

        Assert.True(outcome!.Succeeded, outcome.Message);
        Assert.True(reloaded);
        Assert.Contains("2", File.ReadAllText(_paths.ConfigFile));
        Assert.Contains("1", File.ReadAllText(_paths.ConfigFile + BackupRestore.PreRestoreSuffix));
    }

    [Fact]
    public void Leaves_everything_alone_when_the_backup_is_for_another_database()
    {
        CreateDatabase(_dbPath, "current");
        StageBackup(provider: "postgres");

        var outcome = Apply();

        Assert.False(outcome!.Succeeded);
        Assert.Contains("postgres", outcome.Message);
        Assert.Equal("current", ReadMarker(_dbPath));
        Assert.False(File.Exists(_paths.PendingRestoreFile));
        Assert.True(File.Exists(Path.Combine(_paths.ConfigDirectory, "restore-failed.zip")));
        Assert.Null(Apply()); // not retried on the next start
    }

    [Fact]
    public void Leaves_everything_alone_when_the_database_is_damaged()
    {
        CreateDatabase(_dbPath, "current");
        var damaged = new byte[8192];
        "SQLite format 3\0"u8.CopyTo(damaged);
        new Random(42).NextBytes(damaged.AsSpan(100));
        StageBackup(database: damaged);

        var outcome = Apply();

        Assert.False(outcome!.Succeeded);
        Assert.Equal("current", ReadMarker(_dbPath));
        Assert.False(File.Exists(_dbPath + ".restore"));
        Assert.False(Directory.Exists(_paths.KeysDirectory + BackupRestore.PreRestoreSuffix));
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("keys/../../evil.xml")]
    [InlineData("keys/sub/evil.xml")]
    [InlineData("other.txt")]
    public void Rejects_archives_with_unexpected_entries(string entry)
    {
        StageBackup(extra: zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
            writer.Write("x");
        });

        var (_, error) = BackupArchive.Validate(_paths.PendingRestoreFile, "sqlite");

        Assert.NotNull(error);
        Assert.Contains("Unexpected file", error);
    }
}
