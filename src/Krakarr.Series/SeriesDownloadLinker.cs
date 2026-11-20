using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Krakarr.Downloads;
using Krakarr.Media.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Krakarr.Series;

/// <summary>
/// Links manually added downloads to a library series. The import maps every file to its episodes; the episodes
/// recorded here (from the torrent name) only mark them as downloading, so searches don't grab them again.
/// </summary>
public sealed class SeriesDownloadLinker(KrakarrDbContext db, IEventHub eventHub, TimeProvider time, ILogger<SeriesDownloadLinker> logger)
    : IManualDownloadLinker
{
    public MediaType MediaType => MediaType.Series;
    public string DefaultCategory => "tv";

    public async Task<AddTorrentOptions?> PrepareAsync(int id, AddTorrentOptions options, CancellationToken ct = default)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return null;
        }

        return options with { MediaType = MediaType.Series, SeriesId = id, MovieId = null };
    }

    public async Task OnAddedAsync(Download download, CancellationToken ct = default)
    {
        var seriesId = download.SeriesId ?? throw new InvalidOperationException("Download is not linked to a series");
        var parsed = ReleaseParser.Parse(download.Name);
        int? qualityId = parsed.Quality != Qualities.Unknown ? parsed.Quality.Id : null;

        IReadOnlyList<Episode> episodes = [];
        if (parsed.SeasonNumber is { } season)
        {
            // Only the parsed season's episodes, without their files.
            var candidates = await db.Episodes.AsNoTracking()
                .Where(e => e.SeriesId == seriesId && e.SeasonNumber == season)
                .ToListAsync(ct);
            episodes = EpisodeDecisionEngine.MapEpisodes(candidates, parsed, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        }

        var episodeIds = episodes.Select(e => e.Id).ToList();
        if (qualityId is not null || episodeIds.Count > 0)
        {
            await db.Downloads.Where(d => d.InfoHash == download.InfoHash)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.QualityId, qualityId).SetProperty(d => d.EpisodeIds, episodeIds), ct);
        }

        var entry = new HistoryEntry
        {
            EventType = HistoryEventType.Grabbed,
            SourceTitle = download.Name,
            SeriesId = seriesId,
            QualityId = qualityId,
            Indexer = download.IndexerName,
            Message = episodes.Count > 0 ? $"{SeriesService.Codes(episodes)} (manual download)" : "Manual download",
        };
        db.History.Add(entry);
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(EventNames.HistoryAdded, entry, ct);
        logger.LogInformation("Linked manual download {Name} to series {SeriesId} ({Episodes})", download.Name, seriesId, entry.Message);
    }
}
