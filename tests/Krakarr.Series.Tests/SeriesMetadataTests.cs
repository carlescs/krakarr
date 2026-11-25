using Krakarr.Core.Configuration;
using Krakarr.Core.Metadata;
using Krakarr.Data;

namespace Krakarr.Series.Tests;

public class SeriesMetadataTests
{
    private sealed class Settings(MetadataSettings meta) : ISettingsService
    {
        public Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.FromResult(meta as T ?? new T());
        public Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new() => Task.CompletedTask;
    }

    private sealed class Tmdb : ITmdbTvClient
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default)
        {
            Calls.Add("search");
            return Task.FromResult<IReadOnlyList<SeriesLookup>>([new SeriesLookup { TmdbId = 1396, Title = "Breaking Bad" }]);
        }

        public Task<SeriesInfo> GetSeriesAsync(int tmdbId, CancellationToken ct = default)
        {
            Calls.Add($"get {tmdbId}");
            return Task.FromResult(new SeriesInfo { TmdbId = tmdbId, Title = "TMDB" });
        }

        public Task<SeriesLookup?> FindByTvdbAsync(int tvdbId, CancellationToken ct = default) =>
            Task.FromResult(tvdbId == 81189 ? new SeriesLookup { TmdbId = 1396, Title = "Breaking Bad" } : null);

        public Task<int?> GetTvdbIdAsync(int tmdbId, CancellationToken ct = default) => Task.FromResult<int?>(tmdbId == 1396 ? 81189 : null);
    }

    private sealed class Tvdb : ITvdbClient
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<SeriesLookup>> SearchAsync(string query, CancellationToken ct = default)
        {
            Calls.Add("search");
            return Task.FromResult<IReadOnlyList<SeriesLookup>>([new SeriesLookup { TvdbId = 81189, Title = "Breaking Bad" }]);
        }

        public Task<SeriesInfo> GetSeriesAsync(int tvdbId, CancellationToken ct = default)
        {
            Calls.Add($"get {tvdbId}");
            return Task.FromResult(new SeriesInfo { TvdbId = tvdbId, Title = "TVDB" });
        }
        public Task LoginAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (SeriesMetadata Metadata, Tmdb Tmdb, Tvdb Tvdb) Create(SeriesMetadataSource source)
    {
        var tmdb = new Tmdb();
        var tvdb = new Tvdb();
        return (new SeriesMetadata(new Settings(new MetadataSettings { SeriesSource = source }), tmdb, tvdb), tmdb, tvdb);
    }

    [Fact]
    public async Task Defaults_to_tmdb()
    {
        Assert.Equal(SeriesMetadataSource.Tmdb, new MetadataSettings().SeriesSource);
        var (metadata, tmdb, tvdb) = Create(SeriesMetadataSource.Tmdb);
        await metadata.SearchAsync("breaking", TestContext.Current.CancellationToken);
        Assert.Equal(["search"], tmdb.Calls);
        Assert.Empty(tvdb.Calls);
    }

    [Fact]
    public async Task Uses_tvdb_when_selected()
    {
        var (metadata, tmdb, tvdb) = Create(SeriesMetadataSource.Tvdb);
        var r = Assert.Single(await metadata.SearchAsync("breaking", TestContext.Current.CancellationToken));
        Assert.Equal(81189, r.TvdbId);
        Assert.Empty(tmdb.Calls);
    }

    [Fact]
    public async Task Tmdb_source_resolves_a_tvdb_id()
    {
        var (metadata, tmdb, _) = Create(SeriesMetadataSource.Tmdb);
        var info = await metadata.GetSeriesAsync(new SeriesIds(null, 81189), TestContext.Current.CancellationToken);
        Assert.Equal(["get 1396"], tmdb.Calls);
        Assert.Equal(81189, info.TvdbId); // carried over when TMDB lacks it
        await Assert.ThrowsAsync<MetadataException>(() => metadata.GetSeriesAsync(new SeriesIds(null, 5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tvdb_source_resolves_a_tmdb_id()
    {
        var (metadata, _, tvdb) = Create(SeriesMetadataSource.Tvdb);
        var info = await metadata.GetSeriesAsync(new SeriesIds(1396, null), TestContext.Current.CancellationToken);
        Assert.Equal(["get 81189"], tvdb.Calls);
        Assert.Equal(1396, info.TmdbId);
        await Assert.ThrowsAsync<MetadataException>(() => metadata.GetSeriesAsync(new SeriesIds(7, null), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<MetadataException>(() => metadata.GetSeriesAsync(new SeriesIds(null, null), TestContext.Current.CancellationToken));
    }
}
