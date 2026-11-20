using Krakarr.Core.Domain;

namespace Krakarr.Downloads.Tests;

public sealed class ManualDownloadLinksTests
{
    private sealed class FakeLinker(MediaType type, string category, params int[] known) : IManualDownloadLinker
    {
        public List<Download> Added { get; } = [];
        public MediaType MediaType => type;
        public string DefaultCategory => category;

        public Task<AddTorrentOptions?> PrepareAsync(int id, AddTorrentOptions options, CancellationToken ct = default) =>
            Task.FromResult(known.Contains(id)
                ? options with { MediaType = type, MovieId = type == MediaType.Movie ? id : null, SeriesId = type == MediaType.Series ? id : null }
                : null);

        public Task OnAddedAsync(Download download, CancellationToken ct = default)
        {
            Added.Add(download);
            return Task.CompletedTask;
        }
    }

    private readonly FakeLinker _movies = new(MediaType.Movie, "movies", 1);
    private readonly FakeLinker _series = new(MediaType.Series, "tv", 7);
    private AddTorrentOptions? _used;

    private ManualDownloadLinks Links() => new([_movies, _series]);

    private Task<Download> Add(AddTorrentOptions o)
    {
        _used = o;
        return Task.FromResult(new Download
        {
            InfoHash = "abc", Name = "x", SavePath = "/x", Category = o.Category, MovieId = o.MovieId, SeriesId = o.SeriesId, MediaType = o.MediaType,
        });
    }

    [Fact]
    public async Task Unlinked_download_keeps_the_given_category()
    {
        await Links().AddAsync(new(null, null), "manual", new(), Add, TestContext.Current.CancellationToken);
        Assert.Equal("manual", _used!.Category);
        Assert.Null(_used.MediaType);
        Assert.Empty(_movies.Added);
    }

    [Fact]
    public async Task Unlinked_download_without_category_uses_default()
    {
        await Links().AddAsync(new(null, null), " ", new(), Add, TestContext.Current.CancellationToken);
        Assert.Equal("default", _used!.Category);
    }

    [Fact]
    public async Task Movie_link_sets_media_fields_and_notifies_the_module()
    {
        var row = await Links().AddAsync(new(1, null), null, new(), Add, TestContext.Current.CancellationToken);
        Assert.Equal("movies", _used!.Category);
        Assert.Equal(MediaType.Movie, _used.MediaType);
        Assert.Equal(1, row.MovieId);
        Assert.Same(row, Assert.Single(_movies.Added));
        Assert.Empty(_series.Added);
    }

    [Fact]
    public async Task Series_link_keeps_an_explicit_category()
    {
        await Links().AddAsync(new(null, 7), "anime", new(), Add, TestContext.Current.CancellationToken);
        Assert.Equal("anime", _used!.Category);
        Assert.Equal(7, _used.SeriesId);
        Assert.Single(_series.Added);
    }

    [Fact]
    public async Task Unknown_item_is_rejected_before_adding()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Links().AddAsync(new(99, null), null, new(), Add, TestContext.Current.CancellationToken));
        Assert.Null(_used);
    }

    [Fact]
    public async Task Linking_to_both_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Links().AddAsync(new(1, 7), null, new(), Add, TestContext.Current.CancellationToken));
        Assert.Null(_used);
    }
}
