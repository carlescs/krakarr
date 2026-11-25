using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Health;
using Krakarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads.Tests;

public sealed class StorageHealthCheckTests : IAsyncLifetime
{
    private const long GB = 1024L * 1024 * 1024;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeStorage _storage = new();
    private readonly KrakarrPaths _paths = new() { ConfigDirectory = "/config", DefaultDownloadDirectory = "/data/downloads" };
    private ServiceProvider _services = null!;
    private IServiceScope _scope = null!;

    private KrakarrDbContext Db => _scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
    private ISettingsService Settings => _scope.ServiceProvider.GetRequiredService<ISettingsService>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Paths map to mounts by prefix; <see cref="Writable"/> and <see cref="Empty"/> hold the exceptions.</summary>
    private sealed class FakeStorage : IStorageProbe
    {
        public HashSet<string> Existing { get; } = [];
        public HashSet<string> ReadOnly { get; } = [];
        public HashSet<string> Empty { get; } = [];
        public Dictionary<string, (long Free, long Total)> Mounts { get; } = [];

        public bool DirectoryExists(string path) => Existing.Contains(path);
        public bool HasEntries(string path) => !Empty.Contains(path);
        public bool IsWritable(string path) => !ReadOnly.Contains(path);

        public StorageSpace? GetSpace(string path)
        {
            var mount = Mounts.Keys.Where(m => StorageProbe.IsUnder(path, m)).OrderByDescending(m => m.Length).FirstOrDefault();
            return mount is null ? null : new StorageSpace(mount, Mounts[mount].Free, Mounts[mount].Total);
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        _services = services.BuildServiceProvider();
        _scope = _services.CreateScope();
        await Db.Database.EnsureCreatedAsync();

        Db.RootFolders.AddRange(
            new RootFolder { Path = "/data/movies", MediaType = MediaType.Movie },
            new RootFolder { Path = "/tv", MediaType = MediaType.Series });
        await Db.SaveChangesAsync();
        _storage.Existing.UnionWith(["/data/movies", "/tv", "/data/downloads"]);
        _storage.Mounts["/data"] = (500 * GB, 1000 * GB);
        _storage.Mounts["/tv"] = (500 * GB, 1000 * GB);
    }

    public async ValueTask DisposeAsync()
    {
        _scope.Dispose();
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private RootFolderHealthCheck RootFolders() => new(Db, _storage);
    private DiskSpaceHealthCheck DiskSpace() => new(Db, Settings, _paths, _storage);
    private DownloadFolderHealthCheck DownloadFolder() => new(Db, Settings, _paths, _storage);

    [Theory]
    [InlineData(500, 1000, null)]
    [InlineData(11, 100, null)]
    [InlineData(9, 100, HealthSeverity.Warning)] // below 10 %
    [InlineData(1, 100, HealthSeverity.Error)] // below 2 %
    [InlineData(60, 10_000, null)] // 10 % of a big disk is too much to ask: 50 GB is enough
    [InlineData(49, 10_000, HealthSeverity.Warning)]
    [InlineData(4, 10_000, HealthSeverity.Error)] // under 5 GB
    [InlineData(6, 10_000, HealthSeverity.Warning)]
    [InlineData(1, 0, null)] // unknown size
    public void Disk_space_thresholds(long freeGb, long totalGb, HealthSeverity? expected) =>
        Assert.Equal(expected, DiskSpaceHealthCheck.SeverityFor(freeGb * GB, totalGb * GB));

    [Fact]
    public async Task Healthy_storage_reports_nothing()
    {
        Assert.Empty(await RootFolders().CheckAsync(Ct));
        Assert.Empty(await DiskSpace().CheckAsync(Ct));
        Assert.Single(await DownloadFolder().CheckAsync(Ct)); // only the hardlink warning for /tv on another mount
    }

    [Fact]
    public async Task Missing_and_read_only_root_folders_are_errors()
    {
        _storage.Existing.Remove("/tv");
        _storage.ReadOnly.Add("/data/movies");

        var problems = await RootFolders().CheckAsync(Ct);

        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal(HealthSeverity.Error, p.Severity));
        Assert.Contains("/data/movies is read-only", problems[0].Message);
        Assert.Contains("/tv is missing", problems[1].Message);
    }

    [Fact]
    public async Task An_empty_root_folder_looks_unmounted_only_when_the_library_has_files_there()
    {
        _storage.Empty.Add("/data/movies");
        Assert.Empty(await RootFolders().CheckAsync(Ct)); // new, empty library

        var file = new MovieFile { RelativePath = "Movie (2020).mkv", MovieId = 0 };
        var movie = new Movie { Title = "Movie", Path = "/data/movies/Movie (2020)", TmdbId = 1, QualityProfileId = await ProfileIdAsync() };
        Db.Movies.Add(movie);
        await Db.SaveChangesAsync(Ct);
        file.MovieId = movie.Id;
        Db.MovieFiles.Add(file);
        await Db.SaveChangesAsync(Ct);
        movie.MovieFileId = file.Id;
        await Db.SaveChangesAsync(Ct);

        var problem = Assert.Single(await RootFolders().CheckAsync(Ct));
        Assert.Contains("is empty, but 1 title(s)", problem.Message);
    }

    [Fact]
    public async Task Low_space_is_reported_once_per_filesystem()
    {
        _storage.Mounts["/data"] = (4 * GB, 1000 * GB);

        var problem = Assert.Single(await DiskSpace().CheckAsync(Ct));

        Assert.Equal("/data", problem.Key);
        Assert.Equal(HealthSeverity.Error, problem.Severity);
        Assert.Contains("movies root folder /data/movies", problem.Message);
        Assert.Contains("downloads /data/downloads", problem.Message);
        Assert.Contains("Only 4 GB of 1000 GB free", problem.Message);
    }

    [Fact]
    public async Task Hardlink_warning_names_root_folders_on_another_filesystem()
    {
        var problem = Assert.Single(await DownloadFolder().CheckAsync(Ct));
        Assert.StartsWith("hardlink.", problem.Key);
        Assert.Contains("root folder /tv", problem.Message);

        await Settings.SaveAsync(new MediaManagementSettings { UseHardlinks = false }, Ct);
        Assert.Empty(await DownloadFolder().CheckAsync(Ct));
    }

    [Fact]
    public async Task Built_in_download_folder_must_exist_and_be_writable()
    {
        _storage.ReadOnly.Add("/data/downloads");
        Assert.Contains(await DownloadFolder().CheckAsync(Ct), p => p.Key == "readonly" && p.Severity == HealthSeverity.Error);

        _storage.Existing.Remove("/data/downloads");
        var missing = Assert.Single(await DownloadFolder().CheckAsync(Ct));
        Assert.Equal(("missing", HealthSeverity.Error), (missing.Key, missing.Severity));
    }

    [Fact]
    public async Task QBittorrent_download_folder_comes_from_the_mapped_save_path()
    {
        var settings = new DownloadClientSettings
        {
            Client = DownloadClientKind.QBittorrent,
            QBittorrent = new QBittorrentSettings { SavePath = "/downloads/krakarr", RemotePathPrefix = "/downloads", LocalPathPrefix = "/data/downloads" },
        };
        Assert.Equal("/data/downloads/krakarr", Downloads.DownloadFolder.Resolve(settings, _paths));
        Assert.Equal("/data/downloads", Downloads.DownloadFolder.Resolve(settings with { QBittorrent = settings.QBittorrent with { SavePath = null } }, _paths));
        Assert.Null(Downloads.DownloadFolder.Resolve(new DownloadClientSettings { Client = DownloadClientKind.QBittorrent }, _paths));

        // qBittorrent creates its folder itself and writes it, so a missing one only warns and read-only isn't checked.
        await Settings.SaveAsync(settings, Ct);
        _storage.ReadOnly.Add("/data/downloads/krakarr");
        var problem = Assert.Single(await DownloadFolder().CheckAsync(Ct));
        Assert.Equal(("missing", HealthSeverity.Warning), (problem.Key, problem.Severity));
    }

    private async Task<int> ProfileIdAsync()
    {
        var profile = new QualityProfile { Name = "Any", AllowedQualityIds = [1], CutoffQualityId = 1 };
        Db.QualityProfiles.Add(profile);
        await Db.SaveChangesAsync(Ct);
        return profile.Id;
    }
}
