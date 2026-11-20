using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media.Parsing;
using Krakarr.Series.LibraryImport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Series.Tests;

public class SeriesMatcherTests
{
    private static SeriesLookup L(int id, string title, int year) => new() { TmdbId = id, Title = title, Year = year };

    [Fact]
    public void Matches_title_with_country_suffix_and_year()
    {
        var m = SeriesMatcher.Pick(FolderNameParser.Parse("The Office (US)"), [L(1, "The Office", 2001), L(73244, "The Office (US)", 2005)]);
        Assert.Equal(73244, m.Series!.TmdbId);
        Assert.Equal(MatchConfidence.High, m.Confidence); // unique title match
        var withYear = SeriesMatcher.Pick(FolderNameParser.Parse("Doctor Who (2005)"), [L(76107, "Doctor Who", 1963), L(78804, "Doctor Who (2005)", 2005)]);
        Assert.Equal(78804, withYear.Series!.TmdbId);
        Assert.Equal(MatchConfidence.High, withYear.Confidence);
    }

    [Fact]
    public void Tmdb_id_in_folder_wins()
    {
        var m = SeriesMatcher.Pick(FolderNameParser.Parse("Anything {tmdb-1396}"), [L(1, "Anything", 2000)]);
        Assert.Equal(1396, m.Series!.TmdbId);
        Assert.Equal(MatchConfidence.Exact, m.Confidence);
    }

    [Fact]
    public void Low_when_no_title_matches()
    {
        Assert.Equal(MatchConfidence.Low, SeriesMatcher.Pick(FolderNameParser.Parse("Random"), [L(1, "Other", 2000)]).Confidence);
        Assert.Equal(MatchConfidence.None, SeriesMatcher.Pick(FolderNameParser.Parse("Random"), []).Confidence);
    }

    [Theory]
    [InlineData("Season 01/Show.S01E02.1080p.mkv", 1, new[] { 2 })]
    [InlineData("Show.S02E03E04.mkv", 2, new[] { 3, 4 })]
    [InlineData("Season 3/Episode 07.mkv", 3, new[] { 7 })]
    [InlineData("S04/05 - The Title.mkv", 4, new[] { 5 })]
    [InlineData("Specials/E01 Behind the scenes.mkv", 0, new[] { 1 })]
    [InlineData("Season 1/1x09.mkv", 1, new[] { 9 })]
    public void Identifies_episodes(string relative, int season, int[] episodes)
    {
        var id = SeriesLibraryImport.Identify("/tv/Show", Path.Combine("/tv/Show", relative));
        Assert.NotNull(id);
        Assert.Equal(season, id.Value.Season);
        Assert.Equal(episodes, id.Value.Episodes);
    }

    [Theory]
    [InlineData("random.mkv")]
    [InlineData("Extras/Making of.mkv")]
    public void Ignores_files_without_episode_numbers(string relative) =>
        Assert.Null(SeriesLibraryImport.Identify("/tv/Show", Path.Combine("/tv/Show", relative)));
}

public sealed class SeriesLibraryImportTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-tvimport");
    private ServiceProvider _services = null!;

    private sealed class FakeTmdb : ITmdbTvClient
    {
        public Task<SeriesLookup?> FindByTvdbAsync(int tvdbId, CancellationToken ct = default) =>
            Task.FromResult(tvdbId == 424242 ? new SeriesLookup { TmdbId = 9001, Title = "Test Show", Year = 2021 } : null);

        public Task<int?> GetTvdbIdAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult<int?>(tmdbId == 9001 ? 424242 : null);

        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SeriesLookup>>(query.Contains("test show", StringComparison.OrdinalIgnoreCase)
                ? [new SeriesLookup { TmdbId = 9001, Title = "Test Show", Year = 2021 }] : []);

        public Task<SeriesInfo> GetSeriesAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult(new SeriesInfo
        {
            TmdbId = 9001, TvdbId = 424242, Title = "Test Show", Year = 2021,
            Episodes =
            [
                new EpisodeInfo { TmdbId = 1, SeasonNumber = 0, EpisodeNumber = 1, AirDate = new DateOnly(2021, 6, 1) },
                new EpisodeInfo { TmdbId = 2, SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2021, 1, 1) },
                new EpisodeInfo { TmdbId = 3, SeasonNumber = 1, EpisodeNumber = 2, AirDate = new DateOnly(2021, 1, 8) },
                new EpisodeInfo { TmdbId = 4, SeasonNumber = 1, EpisodeNumber = 3, AirDate = new DateOnly(2021, 1, 15) },
                new EpisodeInfo { TmdbId = 5, SeasonNumber = 1, EpisodeNumber = 4, AirDate = new DateOnly(2021, 1, 22) },
            ],
        });
    }

    /// <summary>TheTVDB's view of the same show: different episode ids, and a renamed second episode.</summary>
    private sealed class FakeTvdb : ITvdbClient
    {
        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SeriesLookup>>([new SeriesLookup { TvdbId = 424242, Title = "Test Show", Year = 2021 }]);

        public Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default) => Task.FromResult(new SeriesInfo
        {
            TvdbId = 424242, Title = "Test Show (TVDB)", Year = 2021,
            Episodes =
            [
                new EpisodeInfo { TvdbId = 71, SeasonNumber = 1, EpisodeNumber = 1, AirDate = new DateOnly(2021, 1, 1) },
                new EpisodeInfo { TvdbId = 72, SeasonNumber = 1, EpisodeNumber = 2, Title = "Renamed", AirDate = new DateOnly(2021, 1, 8) },
            ],
        });
    }

    private sealed class Unused : IIndexerSearchService, IDownloadService
    {
        public Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> GrabAsync(ReleaseInfo release, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddSingleton<ITmdbTvClient, FakeTmdb>();
        services.AddSingleton<ITvdbClient, FakeTvdb>();
        services.AddScoped<ISeriesMetadata, SeriesMetadata>();
        services.AddSingleton<IIndexerSearchService, Unused>();
        services.AddSingleton<IDownloadService, Unused>();
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<SeriesService>();
        services.AddScoped<SeriesLibraryImport>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.QualityProfiles.Add(new QualityProfile { Name = "HD", AllowedQualityIds = [16], CutoffQualityId = 16 });
        // Series folders must be direct children of a configured root folder.
        db.RootFolders.Add(new RootFolder { Path = _root.FullName, MediaType = MediaType.Series });
        db.RootFolders.Add(new RootFolder { Path = "/tv", MediaType = MediaType.Series });
        await db.SaveChangesAsync();

        var show = Path.Combine(_root.FullName, "Test Show (2021)");
        Write(show, "Season 01/Test.Show.S01E01E02.1080p.WEB-DL.x264-GRP.mkv", 4000);
        Write(show, "Season 01/Test.Show.S01E02.720p.HDTV.x264-OLD.mkv", 1000);  // duplicate, worse quality
        Write(show, "Season 01/Test.Show.S01E03.1080p.WEB-DL.x264-GRP.mkv", 2000);
        Write(show, "Season 01/Test.Show.S01E09.1080p.WEB-DL.x264-GRP.mkv", 2000); // unknown episode
        Write(show, "Specials/E01 Behind the Scenes.mkv", 1500);
        Write(show, "Extras/Bloopers.mkv", 900);
        Write(show, "Season 01/Test.Show.S01E01.en.srt", 10);
    }

    private static void Write(string dir, string relative, int bytes)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _root.Delete(true);
    }

    [Fact]
    public async Task Scans_and_imports_existing_episode_files()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<SeriesLibraryImport>();
        var root = await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().RootFolders.FirstAsync(r => r.Path == _root.FullName, ct);

        var scan = await import.ScanAsync(root, ct);
        var row = Assert.Single(scan);
        Assert.Equal(9001, row.Match!.TmdbId);
        Assert.Equal(MatchConfidence.High, row.Confidence);
        Assert.Equal(6, row.VideoFileCount);
        Assert.Equal(5, row.EpisodeFileCount); // Bloopers has no episode number

        var summary = await import.ImportAsync(new SeriesImportItem(row.Path, 9001, null, 1), ct);
        Assert.StartsWith("Test Show: 4 episode(s) linked", summary);
        Assert.Contains("S01E02.720p", summary.Replace("Test.Show.", ""));
        Assert.Contains("(duplicate)", summary);
        Assert.Contains("S01E09 unknown", summary);
        Assert.Contains("Bloopers.mkv (no episode number)", summary);

        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var series = await db.Series.SingleAsync(ct);
        Assert.Equal(row.Path, series.Path);
        Assert.True(series.SeasonFolder);
        var episodes = await db.Episodes.Include(e => e.EpisodeFile).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToListAsync(ct);
        Assert.Equal(Path.Combine("Specials", "E01 Behind the Scenes.mkv"), episodes[0].EpisodeFile!.RelativePath);
        Assert.Equal(episodes[1].EpisodeFileId, episodes[2].EpisodeFileId); // multi-episode file shared by E01+E02
        Assert.Equal(Qualities.WebDl1080.Id, episodes[2].EpisodeFile!.QualityId);
        Assert.NotNull(episodes[3].EpisodeFile);
        Assert.Null(episodes[4].EpisodeFile); // S01E04 missing
        Assert.Equal(3, await db.EpisodeFiles.CountAsync(ct));
        Assert.True(File.Exists(Path.Combine(series.Path, episodes[1].EpisodeFile!.RelativePath))); // untouched

        Assert.Empty(await import.ScanAsync(root, ct)); // already imported
    }

    [Fact]
    public async Task Tvdb_tag_in_folder_is_resolved_through_tmdb()
    {
        using var scope = _services.CreateScope();
        var import = scope.ServiceProvider.GetRequiredService<SeriesLibraryImport>();
        var ct = TestContext.Current.CancellationToken;
        var m = await import.MatchAsync(FolderNameParser.Parse("Something Else {tvdb-424242}"), ct);
        Assert.Equal(424242, m.Series!.TvdbId);
        Assert.Equal(MatchConfidence.Exact, m.Confidence);

        // Adding by the TVDB id alone resolves the TMDB show while TMDB is the source.
        var added = await scope.ServiceProvider.GetRequiredService<SeriesService>().AddAsync(new AddSeriesRequest(null, 424242, 1, "/tv"), ct);
        Assert.Equal(9001, added.TmdbId);
        Assert.Equal(424242, added.TvdbId);
        Assert.Equal("Test Show", added.Title);
    }

    [Fact]
    public async Task Switching_the_source_to_tvdb_refreshes_from_tvdb_and_keeps_episodes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SeriesService>();
        var added = await service.AddAsync(new AddSeriesRequest(9001, null, 1, "/tv"), ct);
        var s01e02 = added.Episodes.Single(e => e is { SeasonNumber: 1, EpisodeNumber: 2 }).Id;

        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SaveAsync(new MetadataSettings { SeriesSource = SeriesMetadataSource.Tvdb }, ct);
        await service.RefreshAsync((await service.LoadAsync(added.Id, ct))!, ct);

        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var series = await db.Series.AsNoTracking().Include(x => x.Episodes).SingleAsync(ct);
        Assert.Equal("Test Show (TVDB)", series.Title);
        Assert.Equal(9001, series.TmdbId); // kept
        Assert.Equal(2, series.Episodes.Count); // TVDB's list wins
        var e2 = series.Episodes.Single(e => e.EpisodeNumber == 2);
        Assert.Equal(s01e02, e2.Id);
        Assert.Equal("Renamed", e2.Title);
        Assert.Equal(72, e2.TvdbId);
        Assert.Equal(3, e2.TmdbId); // TMDB id kept for switching back
    }

    [Fact]
    public async Task Refresh_resolves_series_added_with_tvdb_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SeriesService>();

        // As left by the migrations: TVDB id kept, TMDB id unknown, episode ids cleared.
        var legacy = new Core.Domain.Series { TvdbId = 424242, Title = "Test Show", Path = "/tv/Test Show", QualityProfileId = 1 };
        legacy.Seasons.Add(new Season { SeasonNumber = 1 });
        legacy.Episodes.Add(new Episode { SeasonNumber = 1, EpisodeNumber = 1, Monitored = true });
        db.Series.Add(legacy);
        await db.SaveChangesAsync(ct);
        var keptEpisodeId = legacy.Episodes[0].Id;

        await service.RefreshAsync((await service.LoadAsync(legacy.Id, ct))!, ct);

        var series = await db.Series.AsNoTracking().Include(x => x.Episodes).SingleAsync(ct);
        Assert.Equal(9001, series.TmdbId);
        Assert.Equal(424242, series.TvdbId);
        Assert.Equal(5, series.Episodes.Count);
        var s01e01 = series.Episodes.Single(e => e.SeasonNumber == 1 && e.EpisodeNumber == 1);
        Assert.Equal(keptEpisodeId, s01e01.Id); // matched by season/number, not recreated
        Assert.Equal(2, s01e01.TmdbId);
    }
}
