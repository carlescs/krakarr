using Krakarr.Core.Domain;

namespace Krakarr.Downloads;

/// <summary>
/// Lets a media module (movies, series) take ownership of a manually added download, so it is imported into the
/// library like a grab from that module. The Downloads module only knows the interface; each module registers one.
/// </summary>
public interface IManualDownloadLinker
{
    MediaType MediaType { get; }

    /// <summary>Category used when the user did not pick one (it also names the built-in client's save folder).</summary>
    string DefaultCategory { get; }

    /// <summary>Fills in the media fields of <paramref name="options"/>, or returns null when the item does not exist.</summary>
    Task<AddTorrentOptions?> PrepareAsync(int id, AddTorrentOptions options, CancellationToken ct = default);

    /// <summary>
    /// Runs once the client accepted the torrent (whose name is now known): records the grab in the history and
    /// fills in what can be derived from the name, such as the quality and the episodes it contains.
    /// </summary>
    Task OnAddedAsync(Download download, CancellationToken ct = default);
}

/// <summary>Optional link of a manual download to a library item. At most one of the ids may be set.</summary>
public sealed record ManualDownloadLink(int? MovieId, int? SeriesId)
{
    public bool IsEmpty => MovieId is null && SeriesId is null;
}

/// <summary>Applies a <see cref="ManualDownloadLink"/> around adding a torrent.</summary>
public sealed class ManualDownloadLinks(IEnumerable<IManualDownloadLinker> linkers)
{
    /// <summary>
    /// Adds a torrent with <paramref name="add"/>, linked to the requested item (if any).
    /// Throws <see cref="ArgumentException"/> for an invalid link and <see cref="KeyNotFoundException"/> for an unknown item.
    /// </summary>
    public async Task<Download> AddAsync(
        ManualDownloadLink link, string? category, AddTorrentOptions options, Func<AddTorrentOptions, Task<Download>> add, CancellationToken ct)
    {
        if (link.MovieId is not null && link.SeriesId is not null)
        {
            throw new ArgumentException("Link a download to a movie or to a series, not both");
        }

        var (type, id) = link.MovieId is { } movieId ? (MediaType.Movie, movieId)
            : link.SeriesId is { } seriesId ? (MediaType.Series, seriesId)
            : (default(MediaType?), 0);
        var linker = type is null ? null
            : linkers.FirstOrDefault(l => l.MediaType == type) ?? throw new ArgumentException($"Cannot link downloads to {type}");

        options = options with
        {
            Category = !string.IsNullOrWhiteSpace(category) ? category.Trim() : linker?.DefaultCategory ?? "default",
        };

        if (linker is null)
        {
            return await add(options);
        }

        options = await linker.PrepareAsync(id, options, ct)
            ?? throw new KeyNotFoundException(type == MediaType.Movie ? "Movie not found" : "Series not found");
        var download = await add(options);
        await linker.OnAddedAsync(download, ct);
        return download;
    }
}
