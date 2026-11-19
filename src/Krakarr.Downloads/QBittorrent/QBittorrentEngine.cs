using System.Collections.Concurrent;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;

namespace Krakarr.Downloads.QBittorrent;

/// <summary>Creates API clients; overridable so tests can inject a fake HTTP handler.</summary>
public class QBittorrentApiFactory
{
    public virtual QBittorrentApi Create(QBittorrentSettings settings) => new(settings);
}

/// <summary>
/// Uses an external qBittorrent as the download client. Every torrent Krakarr adds is tagged with the configured tag,
/// and only torrents carrying that tag are listed, synchronised or controlled.
/// </summary>
public sealed class QBittorrentEngine(
    IServiceScopeFactory scopeFactory,
    QBittorrentApiFactory apiFactory,
    IEventHub eventHub,
    ILogger<QBittorrentEngine> logger) : BackgroundService, IDownloadClientImpl
{
    public const string RemovedMessage = "Removed from qBittorrent";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    /// <summary>Freshly added torrents may not be listed yet; don't treat them as removed.</summary>
    private static readonly TimeSpan AddGracePeriod = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _apiLock = new(1, 1);
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private ConcurrentDictionary<string, TorrentStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private QBittorrentApi? _api;
    private DateTimeOffset _lastErrorLog = DateTimeOffset.MinValue;

    public DownloadClientKind Kind => DownloadClientKind.QBittorrent;

    public string? LastError { get; private set; }

    // ---- Background sync ----------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = await GetSettingsAsync(stoppingToken);
                if (settings.Client != DownloadClientKind.QBittorrent)
                {
                    if (!_statuses.IsEmpty) _statuses = new(StringComparer.OrdinalIgnoreCase);
                    LastError = null;
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                try
                {
                    await SyncAsync(settings, stoppingToken);
                    LastError = null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    if (DateTimeOffset.UtcNow - _lastErrorLog > TimeSpan.FromMinutes(1))
                    {
                        _lastErrorLog = DateTimeOffset.UtcNow;
                        logger.LogWarning("qBittorrent sync failed: {Message}", ex.Message);
                    }

                    _statuses = new(StringComparer.OrdinalIgnoreCase);
                    await eventHub.PublishAsync(EventNames.DownloadsUpdated, Array.Empty<TorrentStatus>(), stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Pulls the tagged torrents from qBittorrent and reconciles them with the Download table.</summary>
    public async Task SyncAsync(DownloadClientSettings settings, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var qbt = settings.QBittorrent;
            var api = await GetApiAsync(qbt, ct);
            var torrents = await api.GetTorrentsAsync(qbt.Tag, ct: ct);
            var index = new Dictionary<string, QbtTorrent>(StringComparer.OrdinalIgnoreCase);
            foreach (var torrent in torrents)
            {
                foreach (var key in torrent.Keys) index.TryAdd(key, torrent);
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            var rows = await db.Downloads.Where(d => d.Client == DownloadClients.QBittorrent).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;

            // Pair DB rows with qBittorrent torrents (by any of the torrent's hashes).
            var matched = new List<(Download Row, QbtTorrent Torrent)>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (index.TryGetValue(row.InfoHash, out var torrent) && claimed.Add(torrent.Hash))
                {
                    matched.Add((row, torrent));
                }
            }

            // Torrents tagged by the user directly in qBittorrent: track them as manual downloads.
            foreach (var torrent in torrents.Where(t => !claimed.Contains(t.Hash)))
            {
                var keys = torrent.Keys.ToList();
                if (await db.Downloads.AnyAsync(d => keys.Contains(d.InfoHash), ct))
                {
                    continue; // owned by the built-in client
                }

                var row = new Download
                {
                    Client = DownloadClients.QBittorrent,
                    InfoHash = torrent.KrakarrHash,
                    Name = torrent.Name,
                    Category = string.IsNullOrEmpty(torrent.Category) ? "default" : torrent.Category,
                    SavePath = MapPath(torrent.SavePath ?? string.Empty, qbt),
                    Added = torrent.AddedOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn) : now,
                };
                db.Downloads.Add(row);
                claimed.Add(torrent.Hash);
                matched.Add((row, torrent));
            }

            foreach (var (row, torrent) in matched)
            {
                row.Name = torrent.Name;
                row.Size = torrent.Size > 0 ? torrent.Size : torrent.TotalSize;
                row.UploadedBytes = torrent.Uploaded;
                row.DownloadedBytes = torrent.Downloaded;
                row.SavePath = MapPath(torrent.SavePath ?? row.SavePath, qbt);
                row.State = MapState(torrent.State, torrent.Progress);
                row.Paused = row.State == DownloadState.Paused;
                row.ErrorMessage = row.State == DownloadState.Error ? $"qBittorrent state: {torrent.State}" : null;
                if (torrent.Progress >= 1 && row.CompletedAt is null)
                {
                    row.CompletedAt = torrent.CompletionOn > 0 ? DateTimeOffset.FromUnixTimeSeconds(torrent.CompletionOn) : now;
                    logger.LogInformation("qBittorrent download completed: {Name}", row.Name);
                }

                if (row.ImportMessage == RemovedMessage)
                {
                    row.ImportMessage = null; // it came back
                }
            }

            // Rows whose torrent disappeared from qBittorrent (or lost our tag).
            var matchedRows = matched.Select(m => m.Row).ToHashSet();
            foreach (var row in rows.Where(r => !matchedRows.Contains(r)))
            {
                if (now - row.Added < AddGracePeriod)
                {
                    continue;
                }

                if (row.Imported || (row.MovieId is null && row.SeriesId is null))
                {
                    db.Downloads.Remove(row);
                }
                else if (row.ImportMessage is null)
                {
                    row.ImportMessage = RemovedMessage; // frees the movie for a new search
                }
            }

            await db.SaveChangesAsync(ct);

            var statuses = new ConcurrentDictionary<string, TorrentStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var (row, torrent) in matched)
            {
                statuses[row.InfoHash] = BuildStatus(row, torrent);
            }

            _statuses = statuses;
            await eventHub.PublishAsync(EventNames.DownloadsUpdated, GetStatuses(), ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // ---- ITorrentEngine ----------------------------------------------------------------------

    public async Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default)
    {
        if (!MagnetLink.TryParse(magnetUri, out var magnet) || magnet is null)
        {
            throw new ArgumentException("Invalid magnet link", nameof(magnetUri));
        }

        var hash = magnet.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        return await AddAsync(hash, magnet.Name ?? options.DisplayName ?? hash, magnet.Size ?? 0, magnetUri, null, options, ct);
    }

    public async Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default)
    {
        Torrent torrent;
        try
        {
            torrent = Torrent.Load(torrentData);
        }
        catch (Exception ex)
        {
            throw new ArgumentException("Invalid torrent file", nameof(torrentData), ex);
        }

        var hash = torrent.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        return await AddAsync(hash, torrent.Name, torrent.Size, null, torrentData, options, ct);
    }

    private async Task<Download> AddAsync(string hash, string name, long size, string? magnet, byte[]? file, AddTorrentOptions options, CancellationToken ct)
    {
        var settings = (await GetSettingsAsync(ct)).QBittorrent;
        var api = await GetApiAsync(settings, ct);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        if (await db.Downloads.AnyAsync(d => d.InfoHash == hash, ct))
        {
            throw new InvalidOperationException("Torrent already exists");
        }

        var category = string.IsNullOrWhiteSpace(settings.Category) ? options.Category : settings.Category;
        var savePath = !string.IsNullOrWhiteSpace(options.SavePath) ? UnmapPath(options.SavePath, settings) : settings.SavePath;

        try
        {
            await api.CreateTagAsync(settings.Tag, ct);
            if (!string.IsNullOrWhiteSpace(category))
            {
                await api.CreateCategoryAsync(category, ct);
            }

            await api.AddAsync(magnet, file, settings.Tag, category, savePath, options.Paused, ct);
        }
        catch (QBittorrentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        var row = new Download
        {
            Client = DownloadClients.QBittorrent,
            InfoHash = hash,
            Name = name,
            Category = string.IsNullOrWhiteSpace(category) ? "default" : category,
            MagnetUri = magnet,
            SavePath = string.IsNullOrWhiteSpace(savePath) ? string.Empty : MapPath(savePath, settings),
            Size = size,
            Paused = options.Paused,
            MediaType = options.MediaType,
            MovieId = options.MovieId,
            SeriesId = options.SeriesId,
            QualityId = options.QualityId,
            IndexerName = options.IndexerName,
        };
        db.Downloads.Add(row);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Added torrent {Name} ({Hash}) to qBittorrent with tag {Tag}", name, hash, settings.Tag);
        return row;
    }

    public async Task PauseAsync(string infoHash)
    {
        var (api, torrent) = await RequireTaggedAsync(infoHash);
        await api.PauseAsync(torrent.Hash);
    }

    public async Task ResumeAsync(string infoHash)
    {
        var (api, torrent) = await RequireTaggedAsync(infoHash);
        await api.ResumeAsync(torrent.Hash);
    }

    public async Task RemoveAsync(string infoHash, bool deleteData)
    {
        var settings = (await GetSettingsAsync(CancellationToken.None)).QBittorrent;
        var api = await GetApiAsync(settings, CancellationToken.None);
        var torrent = await FindTaggedAsync(api, settings, infoHash, CancellationToken.None);
        if (torrent is not null)
        {
            await api.DeleteAsync(torrent.Hash, deleteData);
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var deleted = await db.Downloads.Where(d => d.InfoHash == infoHash && d.Client == DownloadClients.QBittorrent).ExecuteDeleteAsync();
        if (torrent is null && deleted == 0)
        {
            throw new KeyNotFoundException(infoHash);
        }

        _statuses.TryRemove(infoHash, out _);
    }

    public IReadOnlyList<TorrentStatus> GetStatuses() => _statuses.Values.OrderByDescending(s => s.Added).ToList();

    public async Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default)
    {
        var settings = (await GetSettingsAsync(ct)).QBittorrent;
        var api = await GetApiAsync(settings, ct);
        var torrent = await FindTaggedAsync(api, settings, infoHash, ct);
        if (torrent is null)
        {
            return [];
        }

        var files = await api.GetFilesAsync(torrent.Hash, ct);
        var root = MapPath(torrent.SavePath ?? string.Empty, settings);
        return files.Select(f => (Path.Combine(root, f.Name.Replace('\\', '/')), f.Size)).ToList();
    }

    /// <summary>Seeding is governed by qBittorrent's own share limits.</summary>
    public void MarkImported(string infoHash)
    {
    }

    public async Task<DownloadClientTestResult> TestAsync(DownloadClientSettings settings, CancellationToken ct = default)
    {
        var qbt = settings.QBittorrent;
        if (string.IsNullOrWhiteSpace(qbt.Tag))
        {
            return new DownloadClientTestResult(false, null, "A tag is required so Krakarr only manages its own torrents");
        }

        try
        {
            using var api = apiFactory.Create(qbt);
            await api.LoginAsync(ct);
            var version = await api.GetVersionAsync(ct);
            var webApi = await api.GetWebApiVersionAsync(ct);
            await api.CreateTagAsync(qbt.Tag, ct);
            var count = (await api.GetTorrentsAsync(qbt.Tag, ct: ct)).Count;
            return new DownloadClientTestResult(true, $"qBittorrent {version} (WebAPI {webApi})",
                $"{count} torrent(s) currently tagged '{qbt.Tag}'");
        }
        catch (QBittorrentException ex)
        {
            return new DownloadClientTestResult(false, null, ex.Message);
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------

    public static DownloadState MapState(string state, double progress) => state switch
    {
        "metaDL" or "forcedMetaDL" => DownloadState.FetchingMetadata,
        "downloading" or "forcedDL" or "stalledDL" or "checkingDL" or "allocating" => DownloadState.Downloading,
        "queuedDL" => DownloadState.Queued,
        "pausedDL" or "stoppedDL" => DownloadState.Paused,
        "uploading" or "stalledUP" or "queuedUP" or "forcedUP" or "checkingUP" => DownloadState.Seeding,
        "pausedUP" or "stoppedUP" => DownloadState.Completed,
        "error" or "missingFiles" => DownloadState.Error,
        _ => progress >= 1 ? DownloadState.Seeding : DownloadState.Downloading, // moving, checkingResumeData, unknown
    };

    /// <summary>Translates a path as qBittorrent sees it into the path Krakarr sees.</summary>
    public static string MapPath(string remotePath, QBittorrentSettings settings) =>
        Translate(remotePath, settings.RemotePathPrefix, settings.LocalPathPrefix);

    /// <summary>Translates a local path into the path qBittorrent sees.</summary>
    public static string UnmapPath(string localPath, QBittorrentSettings settings) =>
        Translate(localPath, settings.LocalPathPrefix, settings.RemotePathPrefix);

    private static string Translate(string path, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || to is null || string.IsNullOrEmpty(path))
        {
            return path;
        }

        var normalizedPath = path.Replace('\\', '/');
        var prefix = from.Replace('\\', '/').TrimEnd('/');
        if (normalizedPath.Equals(prefix, StringComparison.Ordinal))
        {
            return to;
        }

        if (normalizedPath.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            return to.TrimEnd('/', '\\') + "/" + normalizedPath[(prefix.Length + 1)..];
        }

        return path;
    }

    private static TorrentStatus BuildStatus(Download row, QbtTorrent t) => new()
    {
        Id = row.Id,
        InfoHash = row.InfoHash,
        Name = t.Name,
        State = row.State,
        Progress = Math.Round(t.Progress * 100, 2),
        Size = row.Size,
        Downloaded = t.Downloaded,
        Uploaded = t.Uploaded,
        DownloadRate = t.DownloadSpeed,
        UploadRate = t.UploadSpeed,
        Seeds = t.Seeds,
        Leechers = t.Leechers,
        Ratio = Math.Round(t.Ratio, 3),
        EtaSeconds = t.Eta is > 0 and < 8640000 ? t.Eta : null,
        Category = string.IsNullOrEmpty(t.Category) ? row.Category : t.Category,
        SavePath = row.SavePath,
        Added = row.Added,
        CompletedAt = row.CompletedAt,
        MovieId = row.MovieId,
        Imported = row.Imported,
        ImportMessage = row.ImportMessage,
        ErrorMessage = row.ErrorMessage,
    };

    private async Task<(QBittorrentApi Api, QbtTorrent Torrent)> RequireTaggedAsync(string infoHash)
    {
        var settings = (await GetSettingsAsync(CancellationToken.None)).QBittorrent;
        var api = await GetApiAsync(settings, CancellationToken.None);
        var torrent = await FindTaggedAsync(api, settings, infoHash, CancellationToken.None)
            ?? throw new KeyNotFoundException(infoHash);
        return (api, torrent);
    }

    /// <summary>Finds a torrent carrying our tag by any of its hashes (qBittorrent ID, v1 or v2).</summary>
    private static async Task<QbtTorrent?> FindTaggedAsync(QBittorrentApi api, QBittorrentSettings settings, string infoHash, CancellationToken ct) =>
        (await api.GetTorrentsAsync(settings.Tag, ct: ct)).FirstOrDefault(t => t.Matches(infoHash));

    private async Task<QBittorrentApi> GetApiAsync(QBittorrentSettings settings, CancellationToken ct)
    {
        await _apiLock.WaitAsync(ct);
        try
        {
            var current = _api?.Settings;
            if (_api is null || current!.Url != settings.Url || current.Username != settings.Username || current.Password != settings.Password)
            {
                _api?.Dispose();
                _api = apiFactory.Create(settings);
            }

            return _api;
        }
        catch (QBittorrentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            _apiLock.Release();
        }
    }

    private async Task<DownloadClientSettings> GetSettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<DownloadClientSettings>(ct);
    }

    public override void Dispose()
    {
        _api?.Dispose();
        base.Dispose();
    }
}
