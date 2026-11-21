using System.Diagnostics.Metrics;
using Krakarr.Core.Domain;
using Krakarr.Core.Telemetry;
using Krakarr.Data;
using Krakarr.Downloads;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Host.Telemetry;

/// <summary>Library totals behind the <c>krakarr.library.*</c> gauges.</summary>
public sealed record LibrarySnapshot(
    int MoviesMonitored,
    int MoviesUnmonitored,
    int MoviesDownloaded,
    int MoviesMissing,
    int SeriesMonitored,
    int SeriesUnmonitored,
    int EpisodesDownloaded,
    int EpisodesMissing,
    int EpisodesUnaired,
    int EpisodesUnmonitored,
    long MovieFilesSize,
    long EpisodeFilesSize,
    int SubtitleFiles,
    int PendingRequests,
    int EnabledIndexers);

/// <summary>
/// Observable gauges for the library and the download client. Library totals are recounted with a handful of
/// aggregate queries every few minutes, and only while metric export is on; the gauges read the latest snapshot.
/// Download gauges read the client's in-memory status list at each export.
/// </summary>
public sealed class LibraryStats : IDisposable
{
    private static readonly TimeSpan DownloadsCacheTime = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITorrentEngine _engine;
    private readonly ILogger<LibraryStats> _logger;
    private readonly Meter _meter;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _loop;
    private LibrarySnapshot? _snapshot;
    private (DateTimeOffset At, IReadOnlyList<TorrentStatus> Statuses)? _downloads;

    public LibraryStats(IServiceScopeFactory scopeFactory, ITorrentEngine engine, IMeterFactory meterFactory, ILogger<LibraryStats> logger)
    {
        _scopeFactory = scopeFactory;
        _engine = engine;
        _logger = logger;
        // Same name as KrakarrTelemetry.Meter, so AddMeter("Krakarr") exports both; this instance dies with the host.
        _meter = meterFactory.Create(KrakarrTelemetry.Name, KrakarrTelemetry.Version);

        _meter.CreateObservableGauge("krakarr.library.movies", () => Library(s =>
        [
            M(s.MoviesDownloaded, "krakarr.state", "downloaded"),
            M(s.MoviesMissing, "krakarr.state", "missing"),
            M(s.MoviesUnmonitored, "krakarr.state", "unmonitored"),
        ]), "{movie}", "Movies in the library: downloaded, monitored without a file (missing) and unmonitored without a file.");
        _meter.CreateObservableGauge("krakarr.library.series", () => Library(s =>
        [
            M(s.SeriesMonitored, "krakarr.monitored", true),
            M(s.SeriesUnmonitored, "krakarr.monitored", false),
        ]), "{series}", "Series in the library.");
        _meter.CreateObservableGauge("krakarr.library.episodes", () => Library(s =>
        [
            M(s.EpisodesDownloaded, "krakarr.state", "downloaded"),
            M(s.EpisodesMissing, "krakarr.state", "missing"),
            M(s.EpisodesUnaired, "krakarr.state", "unaired"),
            M(s.EpisodesUnmonitored, "krakarr.state", "unmonitored"),
        ]), "{episode}", "Episodes: downloaded, aired and monitored without a file (missing), not aired yet, unmonitored without a file.");
        _meter.CreateObservableGauge("krakarr.library.size", () => Library(s =>
        [
            M(s.MovieFilesSize, KrakarrTelemetry.MediaTypeTag, "movie"),
            M(s.EpisodeFilesSize, KrakarrTelemetry.MediaTypeTag, "series"),
        ]), "By", "Size of the media files in the library.");
        _meter.CreateObservableGauge("krakarr.library.subtitles", () => Library(s => [M(s.SubtitleFiles)]),
            "{file}", "Subtitle files in the library.");
        _meter.CreateObservableGauge("krakarr.media_requests.pending", () => Library(s => [M(s.PendingRequests)]),
            "{request}", "Requests waiting for approval.");
        _meter.CreateObservableGauge("krakarr.indexers.enabled", () => Library(s => [M(s.EnabledIndexers)]),
            "{indexer}", "Enabled indexers.");

        _meter.CreateObservableGauge("krakarr.downloads.torrents", ObserveTorrents, "{torrent}", "Torrents in the download client, by state.");
        _meter.CreateObservableGauge("krakarr.downloads.rate", ObserveRates, "By/s", "Current transfer rate of the download client.");
        _meter.CreateObservableGauge("krakarr.downloads.remaining", () =>
        {
            long remaining = 0;
            foreach (var t in Statuses()) remaining += Math.Max(0, t.Size - t.Downloaded);
            return new Measurement<long>(remaining);
        }, "By", "Bytes left to download.");
        _meter.CreateObservableGauge("krakarr.downloads.pending_import", () =>
        {
            var count = 0;
            foreach (var t in Statuses())
            {
                if (t.CompletedAt is not null && !t.Imported && t.ImportMessage is null && (t.MovieId is not null || t.SeriesId is not null)) count++;
            }

            return new Measurement<int>(count);
        }, "{torrent}", "Completed media downloads waiting to be imported.");
    }

    public LibrarySnapshot? Snapshot => _snapshot;

    /// <summary>Starts (or restarts) recounting every <paramref name="interval"/>.</summary>
    public void Start(TimeSpan interval)
    {
        Stop();
        var cts = new CancellationTokenSource();
        lock (_lock) _loop = cts;
        _ = Task.Run(() => RunAsync(interval, cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _loop;
            _loop = null;
        }

        cts?.Cancel();
        cts?.Dispose();
        _snapshot = null;
    }

    public void Dispose()
    {
        Stop();
        _meter.Dispose();
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    _snapshot = await CountAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not count the library for telemetry");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A few aggregate queries; nothing is loaded into memory.</summary>
    public async Task<LibrarySnapshot> CountAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var today = DateOnly.FromDateTime(DateTime.Now);

        var movies = await db.Movies.AsNoTracking()
            .GroupBy(m => new { m.Monitored, HasFile = m.MovieFileId != null })
            .Select(g => new { g.Key.Monitored, g.Key.HasFile, Count = g.Count() })
            .ToListAsync(ct);
        var series = await db.Series.AsNoTracking()
            .GroupBy(s => s.Monitored)
            .Select(g => new { Monitored = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var episodesDownloaded = await db.Episodes.CountAsync(e => e.EpisodeFileId != null, ct);
        var episodesUnmonitored = await db.Episodes.CountAsync(e => e.EpisodeFileId == null && !e.Monitored, ct);
        var episodesMissing = await db.Episodes.CountAsync(e => e.EpisodeFileId == null && e.Monitored && e.AirDate != null && e.AirDate <= today, ct);
        var episodesUnaired = await db.Episodes.CountAsync(e => e.EpisodeFileId == null && e.Monitored && (e.AirDate == null || e.AirDate > today), ct);

        return new LibrarySnapshot(
            MoviesMonitored: movies.Where(m => m.Monitored).Sum(m => m.Count),
            MoviesUnmonitored: movies.Where(m => !m.Monitored && !m.HasFile).Sum(m => m.Count),
            MoviesDownloaded: movies.Where(m => m.HasFile).Sum(m => m.Count),
            MoviesMissing: movies.Where(m => m.Monitored && !m.HasFile).Sum(m => m.Count),
            SeriesMonitored: series.Where(s => s.Monitored).Sum(s => s.Count),
            SeriesUnmonitored: series.Where(s => !s.Monitored).Sum(s => s.Count),
            EpisodesDownloaded: episodesDownloaded,
            EpisodesMissing: episodesMissing,
            EpisodesUnaired: episodesUnaired,
            EpisodesUnmonitored: episodesUnmonitored,
            MovieFilesSize: await db.MovieFiles.SumAsync(f => (long?)f.Size, ct) ?? 0,
            EpisodeFilesSize: await db.EpisodeFiles.SumAsync(f => (long?)f.Size, ct) ?? 0,
            SubtitleFiles: await db.SubtitleFiles.CountAsync(ct),
            PendingRequests: await db.MediaRequests.CountAsync(r => r.Status == RequestStatus.Pending, ct),
            EnabledIndexers: await db.Indexers.CountAsync(i => i.Enabled, ct));
    }

    private IEnumerable<Measurement<long>> Library(Func<LibrarySnapshot, Measurement<long>[]> select) =>
        _snapshot is { } s ? select(s) : [];

    private static Measurement<long> M(long value) => new(value);
    private static Measurement<long> M(long value, string key, object tag) => new(value, new KeyValuePair<string, object?>(key, tag));

    private IReadOnlyList<TorrentStatus> Statuses()
    {
        var now = DateTimeOffset.UtcNow;
        if (_downloads is { } d && now - d.At < DownloadsCacheTime) return d.Statuses;
        IReadOnlyList<TorrentStatus> statuses;
        try
        {
            statuses = _engine.GetStatuses();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read download statuses for telemetry");
            statuses = [];
        }

        _downloads = (now, statuses);
        return statuses;
    }

    private IEnumerable<Measurement<int>> ObserveTorrents()
    {
        var counts = new Dictionary<DownloadState, int>();
        foreach (var t in Statuses()) counts[t.State] = counts.GetValueOrDefault(t.State) + 1;
        // Report every state, so a state that empties drops to 0 instead of keeping its last value.
        foreach (var state in Enum.GetValues<DownloadState>())
        {
            yield return new(counts.GetValueOrDefault(state), new KeyValuePair<string, object?>("krakarr.state", state.ToString().ToLowerInvariant()));
        }
    }

    private IEnumerable<Measurement<long>> ObserveRates()
    {
        long down = 0, up = 0;
        foreach (var t in Statuses())
        {
            down += t.DownloadRate;
            up += t.UploadRate;
        }

        yield return new(down, new KeyValuePair<string, object?>("network.io.direction", "receive"));
        yield return new(up, new KeyValuePair<string, object?>("network.io.direction", "transmit"));
    }
}
