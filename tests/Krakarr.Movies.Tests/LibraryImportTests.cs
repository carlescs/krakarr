using Krakarr.Core.Metadata;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Krakarr.Media.Parsing;
using Krakarr.Movies.LibraryImport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies.Tests;

public class MovieMatcherTests
{
    private static MovieLookup L(int id, string title, int year, string? original = null) => new() { TmdbId = id, Title = title, Year = year, OriginalTitle = original };

    [Fact]
    public void High_when_title_and_year_match()
    {
        var m = MovieMatcher.Pick(FolderNameParser.Parse("The Matrix (1999)"), [L(1, "The Matrix Reloaded", 2003), L(603, "The Matrix", 1999)]);
        Assert.Equal(603, m.Movie!.TmdbId);
        Assert.Equal(MatchConfidence.High, m.Confidence);
    }

    [Fact]
    public void Matches_original_title()
    {
        var m = MovieMatcher.Pick(FolderNameParser.Parse("Le fabuleux destin d'Amélie Poulain (2001)"),
            [L(194, "Amélie", 2001, "Le fabuleux destin d'Amélie Poulain")]);
        Assert.Equal(MatchConfidence.High, m.Confidence);
    }

    [Fact]
    public void Medium_when_year_missing_or_off_by_one()
    {
        Assert.Equal(MatchConfidence.Medium, MovieMatcher.Pick(FolderNameParser.Parse("The Matrix"), [L(603, "The Matrix", 1999)]).Confidence);
        Assert.Equal(MatchConfidence.Medium, MovieMatcher.Pick(FolderNameParser.Parse("The Matrix (2000)"), [L(603, "The Matrix", 1999)]).Confidence);
    }

    [Fact]
    public void Low_or_none_otherwise()
    {
        var low = MovieMatcher.Pick(FolderNameParser.Parse("Some Home Video"), [L(5, "Home Alone", 1990)]);
        Assert.Equal(MatchConfidence.Low, low.Confidence);
        Assert.Equal(MatchConfidence.None, MovieMatcher.Pick(FolderNameParser.Parse("Nothing"), []).Confidence);
    }
}

public sealed class MovieLibraryImportTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-libimport");
    private ServiceProvider _services = null!;

    private sealed class FakeTmdb : ITmdbClient
    {
        private static readonly MovieLookup Matrix = new() { TmdbId = 603, ImdbId = "tt0133093", Title = "The Matrix", Year = 1999 };
        public Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MovieLookup>>(query.Contains("matrix", StringComparison.OrdinalIgnoreCase) ? [Matrix] : []);
        public Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default) =>
            tmdbId == 603 ? Task.FromResult(Matrix) : throw new MetadataException("not found");
        public Task<MovieLookup?> FindByImdbAsync(string imdbId, CancellationToken ct = default) =>
            Task.FromResult<MovieLookup?>(imdbId == "tt0133093" ? Matrix : null);
    }

    private sealed class Unused : IIndexerSearchService, IDownloadService
    {
        public Task<IndexerSearchResult> SearchAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IndexerSearchRun> StartAsync(SearchCriteria criteria, IReadOnlyCollection<int>? indexerIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Download> GrabAsync(ReleaseInfo release, AddTorrentOptions options, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<KrakarrDbContext>(o => o.UseSqlite(_connection));
        services.AddKrakarrData();
        services.AddSingleton<ITmdbClient, FakeTmdb>();
        services.AddSingleton<IIndexerSearchService, Unused>();
        services.AddSingleton<IDownloadService, Unused>();
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddScoped<MovieService>();
        services.AddScoped<MovieLibraryImport>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.QualityProfiles.Add(new QualityProfile { Name = "HD", AllowedQualityIds = [17], CutoffQualityId = 17 });
        db.RootFolders.Add(new RootFolder { Path = _root.FullName, MediaType = MediaType.Movie });
        await db.SaveChangesAsync();

        Write("The Matrix (1999)/The.Matrix.1999.1080p.BluRay.x264-SPARKS.mkv", 5000);
        Write("The Matrix (1999)/Sample/sample.mkv", 100);
        Write("The Matrix (1999)/movie.nfo", 10);
        Write("Holiday Clips/clip.mkv", 300);
        Directory.CreateDirectory(Path.Combine(_root.FullName, "Empty Folder"));
    }

    private void Write(string relative, int bytes)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
        _root.Delete(true);
    }

    private async Task<T> With<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = _services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    [Fact]
    public async Task Scans_matches_and_imports_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await With(sp => sp.GetRequiredService<KrakarrDbContext>().RootFolders.SingleAsync(ct));
        var scan = await With(sp => sp.GetRequiredService<MovieLibraryImport>().ScanAsync(root, ct));

        Assert.Equal(["Empty Folder", "Holiday Clips", "The Matrix (1999)"], scan.Select(s => s.FolderName));
        var matrix = scan.Single(s => s.FolderName == "The Matrix (1999)");
        Assert.Equal(MatchConfidence.High, matrix.Confidence);
        Assert.Equal(603, matrix.Match!.TmdbId);
        Assert.Equal("Bluray-1080p", matrix.VideoFile!.Quality);
        Assert.Equal(1, matrix.VideoFileCount); // sample excluded
        Assert.Equal(MatchConfidence.None, scan.Single(s => s.FolderName == "Holiday Clips").Confidence);

        var summary = await With(sp => sp.GetRequiredService<MovieLibraryImport>().ImportAsync(new MovieImportItem(matrix.Path, 603, 1), ct));
        Assert.Equal("The Matrix: Bluray-1080p", summary);

        var movie = await With(sp => sp.GetRequiredService<KrakarrDbContext>().Movies.Include(m => m.MovieFile).SingleAsync(ct));
        Assert.Equal(matrix.Path, movie.Path); // not renamed
        Assert.Equal("The.Matrix.1999.1080p.BluRay.x264-SPARKS.mkv", movie.MovieFile!.RelativePath);
        Assert.Equal("SPARKS", movie.MovieFile.ReleaseGroup);
        Assert.True(File.Exists(Path.Combine(movie.Path, movie.MovieFile.RelativePath))); // not moved

        // Rescan: the imported folder is no longer offered.
        var rescan = await With(sp => sp.GetRequiredService<MovieLibraryImport>().ScanAsync(root, ct));
        Assert.DoesNotContain(rescan, s => s.FolderName == "The Matrix (1999)");
    }

    [Fact]
    public async Task Exact_match_from_folder_id()
    {
        var match = await With(sp => sp.GetRequiredService<MovieLibraryImport>().MatchAsync(FolderNameParser.Parse("Whatever [imdbid-tt0133093]"), TestContext.Current.CancellationToken));
        Assert.Equal(MatchConfidence.Exact, match.Confidence);
        Assert.Equal(603, match.Movie!.TmdbId);
    }
}
