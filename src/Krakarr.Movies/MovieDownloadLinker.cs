using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Movies;

/// <summary>Links manually added downloads to a library movie, so they are imported when they finish.</summary>
public sealed class MovieDownloadLinker(KrakarrDbContext db, IEventHub eventHub, ILogger<MovieDownloadLinker> logger) : IManualDownloadLinker
{
    public MediaType MediaType => MediaType.Movie;
    public string DefaultCategory => "movies";

    public async Task<AddTorrentOptions?> PrepareAsync(int id, AddTorrentOptions options, CancellationToken ct = default)
    {
        if (!await db.Movies.AnyAsync(m => m.Id == id, ct))
        {
            return null;
        }

        return options with { MediaType = MediaType.Movie, MovieId = id, SeriesId = null, EpisodeIds = [] };
    }

    public async Task OnAddedAsync(Download download, CancellationToken ct = default)
    {
        var movieId = download.MovieId ?? throw new InvalidOperationException("Download is not linked to a movie");
        var quality = ReleaseParser.ParseQuality(download.Name);
        int? qualityId = quality != Qualities.Unknown ? quality.Id : null;
        if (qualityId is not null)
        {
            await db.Downloads.Where(d => d.InfoHash == download.InfoHash)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.QualityId, qualityId), ct);
        }

        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.Grabbed,
            SourceTitle = download.Name,
            MovieId = movieId,
            QualityId = qualityId,
            Indexer = download.IndexerName,
            Message = "Manual download",
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        logger.LogInformation("Linked manual download {Name} to movie {MovieId}", download.Name, movieId);
    }
}
