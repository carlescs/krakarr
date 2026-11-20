using System.Text.Json;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Core.Metadata;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Indexers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Movies.Tests;

public class TmdbCollectionMappingTests
{
    [Fact]
    public void Maps_belongs_to_collection()
    {
        const string json = """
            { "id": 120, "title": "The Fellowship of the Ring",
              "belongs_to_collection": { "id": 119, "name": "The Lord of the Rings Collection", "poster_path": "/p.jpg", "backdrop_path": "/b.jpg" } }
            """;
        var movie = JsonSerializer.Deserialize<TmdbClient.TmdbMovie>(json)!;

        var collection = TmdbClient.MapCollection(movie.Collection);

        Assert.NotNull(collection);
        Assert.Equal(119, collection.TmdbId);
        Assert.Equal("The Lord of the Rings Collection", collection.Name);
        Assert.EndsWith("/w500/p.jpg", collection.PosterUrl);
        Assert.EndsWith("/original/b.jpg", collection.FanartUrl);
    }

    [Fact]
    public void No_collection_when_missing_or_null()
    {
        Assert.Null(TmdbClient.MapCollection(JsonSerializer.Deserialize<TmdbClient.TmdbMovie>("""{ "id": 1, "title": "X" }""")!.Collection));
        Assert.Null(TmdbClient.MapCollection(JsonSerializer.Deserialize<TmdbClient.TmdbMovie>("""{ "id": 1, "title": "X", "belongs_to_collection": null }""")!.Collection));
    }
}

public sealed class MovieCollectionServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("krakarr-collections");
    private readonly FakeTmdb _tmdb = new();
    private ServiceProvider _services = null!;

    private sealed class FakeTmdb : ITmdbClient
    {
        public static readonly CollectionLookup Lotr = new(119, "The Lord of the Rings Collection", "https://img/p.jpg", null);
        public Dictionary<int, MovieLookup> Movies { get; } = new()
        {
            [120] = new() { TmdbId = 120, Title = "The Fellowship of the Ring", Year = 2001, Collection = Lotr },
            [121] = new() { TmdbId = 121, Title = "The Two Towers", Year = 2002, Collection = Lotr },
            [603] = new() { TmdbId = 603, Title = "The Matrix", Year = 1999 },
        };

        public Task<IReadOnlyList<MovieLookup>> SearchAsync(string query, int? year, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MovieLookup> GetMovieAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult(Movies[tmdbId]);
        public Task<MovieLookup?> FindByImdbAsync(string imdbId, CancellationToken ct = default) => throw new NotSupportedException();
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
        services.AddSingleton<ITmdbClient>(_tmdb);
        services.AddSingleton<IIndexerSearchService, Unused>();
        services.AddSingleton<IDownloadService, Unused>();
        services.AddSingleton<IEventHub, NullEventHub>();
        services.AddScoped<MovieService>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.QualityProfiles.Add(new QualityProfile { Name = "HD", AllowedQualityIds = [17], CutoffQualityId = 17 });
        db.RootFolders.Add(new RootFolder { Path = _root.FullName, MediaType = MediaType.Movie });
        await db.SaveChangesAsync();
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

    private Task<Movie> Add(int tmdbId) =>
        With(sp => sp.GetRequiredService<MovieService>().AddAsync(new AddMovieRequest(tmdbId, 1, _root.FullName, SearchForMovie: false), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Movies_share_one_collection_row_which_is_pruned_when_unused()
    {
        var ct = TestContext.Current.CancellationToken;
        var fellowship = await Add(120);
        var towers = await Add(121);
        var matrix = await Add(603);

        var collection = await With(sp => sp.GetRequiredService<KrakarrDbContext>().MovieCollections.SingleAsync(ct));
        Assert.Equal(119, collection.TmdbId);
        Assert.Equal("lord of the rings collection", collection.SortName);
        Assert.Equal(collection.Id, fellowship.CollectionId);
        Assert.Equal(collection.Id, towers.CollectionId);
        Assert.Null(matrix.CollectionId);

        // TMDB renames the collection and drops one movie from it; a refresh follows both.
        _tmdb.Movies[120] = _tmdb.Movies[120] with { Collection = FakeTmdb.Lotr with { Name = "LOTR" } };
        _tmdb.Movies[121] = _tmdb.Movies[121] with { Collection = null };
        foreach (var id in new[] { fellowship.Id, towers.Id })
        {
            await With(async sp =>
            {
                var db = sp.GetRequiredService<KrakarrDbContext>();
                await sp.GetRequiredService<MovieService>().RefreshAsync(await db.Movies.SingleAsync(m => m.Id == id, ct), ct);
                return 0;
            });
        }

        var rows = await With(sp => sp.GetRequiredService<KrakarrDbContext>().Movies.AsNoTracking().OrderBy(m => m.TmdbId).Select(m => m.CollectionId).ToListAsync(ct));
        Assert.Equal([collection.Id, null, null], rows);
        Assert.Equal("LOTR", (await With(sp => sp.GetRequiredService<KrakarrDbContext>().MovieCollections.SingleAsync(ct))).Name);

        // Prune keeps collections in use and removes the rest.
        Assert.Equal(0, await With(sp => MovieService.PruneCollectionsAsync(sp.GetRequiredService<KrakarrDbContext>(), ct)));
        _tmdb.Movies[120] = _tmdb.Movies[120] with { Collection = null };
        await With(async sp =>
        {
            var db = sp.GetRequiredService<KrakarrDbContext>();
            await sp.GetRequiredService<MovieService>().RefreshAsync(await db.Movies.SingleAsync(m => m.Id == fellowship.Id, ct), ct);
            return 0;
        });
        Assert.Equal(1, await With(sp => MovieService.PruneCollectionsAsync(sp.GetRequiredService<KrakarrDbContext>(), ct)));
        Assert.False(await With(sp => sp.GetRequiredService<KrakarrDbContext>().MovieCollections.AnyAsync(ct)));
    }
}
