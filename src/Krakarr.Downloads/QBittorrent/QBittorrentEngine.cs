using System.Collections.Concurrent;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
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
/// and only torrents carrying that tag are listed, synchronised or controlled. It syncs while it is the selected client
/// or while it still owns downloads.
/// </summary>
public sealed class QBittorrentEngine(
    IServiceScopeFactory scopeFactory,
    QBittorrentApiFactory apiFactory,
    DownloadStatusFeed statusFeed,
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

    public string Name => DownloadClients.QBittorrent;

    public bool IsActive { get; private set; }

    public string? LastError { get; private set; }

    // ---- Background sync ----------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = await GetSettingsAsync(stoppingToken);
                IsActive = settings.Client == DownloadClientKind.QBittorrent || await OwnsDownloadsAsync(stoppingToken);
                if (!IsActive)
                {
                    if (!_statuses.IsEmpty)
                    {
                        _statuses = new(StringComparer.OrdinalIgnoreCase);
                        await statusFeed.PublishAsync(Kind, [], stoppingToken);
                    }

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
                    await statusFeed.PublishAsync(Kind, [], stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> OwnsDownloadsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Downloads.AnyAsync(d => d.Client == DownloadClients.QBittorrent, ct);
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

            await ApplySeedingPolicyAsync(api, settings, matched, db, now, ct);
            await db.SaveChangesAsync(ct);

            var statuses = new ConcurrentDictionary<string, TorrentStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var (row, torrent) in matched)
            {
                statuses[row.InfoHash] = BuildStatus(row, torrent);
            }

            _statuses = statuses;
            await statusFeed.PublishAsync(Kind, GetStatuses(), ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Removes or stops finished torrents once Krakarr's seeding limit is reached. With both limits at 0 nothing
    /// happens here and qBittorrent's own share limits apply.
    /// </summary>
    private async Task ApplySeedingPolicyAsync(QBittorrentApi api, DownloadClientSettings settings,
        List<(Download Row, QbtTorrent Torrent)> matched, KrakarrDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (row, torrent) in matched.ToList())
        {
            if (torrent.Progress < 1 || !SeedingPolicy.IsDone(settings, row, torrent.Ratio, now))
            {
                continue;
            }

            if (settings.SeedLimitAction == SeedLimitAction.Remove)
            {
                var deleteData = SeedingPolicy.DeleteDataOnRemove(row);
                logger.LogInformation("Seeding limit reached for {Name}, removing it from qBittorrent{Data}", row.Name, deleteData ? " with its data" : string.Empty);
                await api.DeleteAsync(torrent.Hash, deleteData, ct);
                matched.Remove((row, torrent));
                db.Downloads.Remove(row);
            }
            else if (row.State == DownloadState.Seeding)
            {
                logger.LogInformation("Seeding limit reached for {Name}, stopping it in qBittorrent", row.Name);
                await api.PauseAsync(torrent.Hash, ct);
                row.State = DownloadState.Paused;
                row.Paused = true;
            }
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
        if (_statuses.TryGetValue(hash, out var existing))
        {
            throw new InvalidOperationException($"Torrent already exists: {existing.Name}");
        }

        if (await db.Downloads.AnyAsync(d => d.InfoHash == hash, ct))
        {
            // A row whose torrent isn't listed in qBittorrent (removed there, or not synced yet); the router decides.
            throw new StaleDownloadException(hash);
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
            EpisodeIds = options.EpisodeIds.ToList(),
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

    public async Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default)
    {
        var settings = (await GetSettingsAsync(ct)).QBittorrent;
        var api = await GetApiAsync(settings, ct);
        var torrent = await FindTaggedAsync(api, settings, infoHash, ct);
        if (torrent is null || !_statuses.TryGetValue(infoHash, out var status))
        {
            return null;
        }

        var id = Uri.EscapeDataString(torrent.Hash);
        var version = await SafeAsync(() => api.GetVersionAsync(ct));
        using var info = await api.GetJsonAsync($"api/v2/torrents/info?hashes={id}", ct);
        using var props = await api.GetJsonAsync($"api/v2/torrents/properties?hash={id}", ct);
        using var files = await api.GetJsonAsync($"api/v2/torrents/files?hash={id}", ct);
        using var trackers = await api.GetJsonAsync($"api/v2/torrents/trackers?hash={id}", ct);
        using var peers = await SafeAsync(() => api.GetJsonAsync($"api/v2/sync/torrentPeers?hash={id}&rid=0", ct));
        using var pieces = await SafeAsync(() => api.GetJsonAsync($"api/v2/torrents/pieceStates?hash={id}", ct));
        using var webSeeds = await SafeAsync(() => api.GetJsonAsync($"api/v2/torrents/webseeds?hash={id}", ct));

        return MapDetails(status, version, info.RootElement.EnumerateArray().FirstOrDefault(), props.RootElement, files.RootElement,
            trackers.RootElement, peers?.RootElement, pieces?.RootElement, webSeeds?.RootElement);
    }

    /// <summary>Maps qBittorrent WebUI API JSON to <see cref="TorrentDetails"/> (public for tests).</summary>
    public static TorrentDetails MapDetails(
        TorrentStatus status, string? version, System.Text.Json.JsonElement info, System.Text.Json.JsonElement props,
        System.Text.Json.JsonElement files, System.Text.Json.JsonElement trackers, System.Text.Json.JsonElement? peers,
        System.Text.Json.JsonElement? pieces, System.Text.Json.JsonElement? webSeeds)
    {
        static DateTimeOffset? Unix(long? seconds) => seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
        static long? Positive(long? v) => v is >= 0 ? v : null;

        var fileList = files.ValueKind == System.Text.Json.JsonValueKind.Array
            ? files.EnumerateArray().Select((f, i) => new TorrentFileDetails(
                (int)(J.Long(f, "index") ?? i),
                J.Str(f, "name") ?? $"file {i}",
                J.Long(f, "size") ?? 0,
                Math.Round((J.Double(f, "progress") ?? 0) * 100, 2),
                (J.Long(f, "priority") ?? 1) switch { 0 => "Skip", 6 => "High", 7 => "Maximum", _ => "Normal" },
                J.Double(f, "availability") is >= 0 and var a ? Math.Round(a, 3) : null)).ToList()
            : [];

        var trackerList = trackers.ValueKind == System.Text.Json.JsonValueKind.Array
            ? trackers.EnumerateArray().Select(t => (J.Long(t, "status") ?? 1) == 0
                ? new TorrentTrackerDetails { Url = J.Str(t, "url") ?? "?", Tier = (int)(J.Long(t, "tier") ?? -1), Status = "Disabled", Message = J.Str(t, "msg") }
                : new TorrentTrackerDetails
            {
                Url = J.Str(t, "url") ?? "?",
                Tier = (int)(J.Long(t, "tier") ?? -1),
                Status = (J.Long(t, "status") ?? 1) switch
                {
                    0 => "Disabled", 1 => "Not contacted", 2 => "Working", 3 => "Updating", 4 => "Not working", _ => "Unknown",
                },
                Seeds = J.Long(t, "num_seeds") is >= 0 and var s ? (int)s : null,
                Leechers = J.Long(t, "num_leeches") is >= 0 and var l ? (int)l : null,
                Peers = J.Long(t, "num_peers") is >= 0 and var p ? (int)p : null,
                Downloaded = J.Long(t, "num_downloaded") is >= 0 and var d ? (int)d : null,
                Message = J.Str(t, "msg"),
            }).ToList()
            : [];

        var peerList = new List<TorrentPeerDetails>();
        if (peers is { } pr && pr.TryGetProperty("peers", out var peerMap) && peerMap.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in peerMap.EnumerateObject())
            {
                var v = p.Value;
                peerList.Add(new TorrentPeerDetails
                {
                    Address = p.Name,
                    Client = J.Str(v, "client"),
                    Progress = Math.Round((J.Double(v, "progress") ?? 0) * 100, 1),
                    DownloadRate = J.Long(v, "dl_speed") ?? 0,
                    UploadRate = J.Long(v, "up_speed") ?? 0,
                    Downloaded = J.Long(v, "downloaded") ?? 0,
                    Uploaded = J.Long(v, "uploaded") ?? 0,
                    Flags = J.Str(v, "flags"),
                    FlagsDescription = J.Str(v, "flags_desc")?.Replace("\n", ", "),
                    Connection = J.Str(v, "connection"),
                    Country = J.Str(v, "country") is { } country && country != "N/A" ? country : null,
                    IsSeeder = J.Double(v, "progress") is { } prog ? prog >= 1 : null,
                });
            }
        }

        string? pieceMap = null;
        if (pieces is { ValueKind: System.Text.Json.JsonValueKind.Array } ps)
        {
            pieceMap = PieceMap.Build(ps.EnumerateArray().Select(x => x.TryGetInt32(out var n) ? n : 0).ToList());
        }

        var tags = (J.Str(info, "tags") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var category = J.Str(info, "category");
        if (!string.IsNullOrEmpty(category)) tags.Insert(0, category);

        return new TorrentDetails
        {
            Status = status,
            Client = version is null ? "qBittorrent" : $"qBittorrent {version}",
            ClientState = J.Str(info, "state"),
            InfoHashV1 = J.Str(info, "infohash_v1") ?? J.Str(props, "infohash_v1") ?? (J.Str(info, "hash") is { Length: 40 } h ? h : null),
            InfoHashV2 = J.Str(info, "infohash_v2") ?? J.Str(props, "infohash_v2"),
            MagnetUri = J.Str(info, "magnet_uri"),
            Comment = J.Str(props, "comment"),
            CreatedBy = J.Str(props, "created_by"),
            CreationDate = Unix(J.Long(props, "creation_date")),
            IsPrivate = J.Bool(props, "is_private") ?? J.Bool(props, "isPrivate") ?? J.Bool(info, "private"),
            PieceSize = Positive(J.Long(props, "piece_size")),
            PieceCount = J.Long(props, "pieces_num") is > 0 and var pn ? (int)pn : null,
            PiecesHave = J.Long(props, "pieces_have") is >= 0 and var ph ? (int)ph : null,
            TotalSize = Positive(J.Long(info, "total_size") ?? J.Long(props, "total_size")),
            SelectedSize = Positive(J.Long(info, "size")),
            SessionDownloaded = Positive(J.Long(props, "total_downloaded_session")),
            SessionUploaded = Positive(J.Long(props, "total_uploaded_session")),
            Wasted = Positive(J.Long(props, "total_wasted")),
            Availability = J.Double(info, "availability") is >= 0 and var av ? Math.Round(av, 3) : null,
            TimeActiveSeconds = Positive(J.Long(props, "time_elapsed") ?? J.Long(info, "time_active")),
            SeedingTimeSeconds = Positive(J.Long(props, "seeding_time") ?? J.Long(info, "seeding_time")),
            Connections = J.Long(props, "nb_connections") is >= 0 and var nc ? (int)nc : null,
            ConnectionsLimit = J.Long(props, "nb_connections_limit") is > 0 and var ncl ? (int)ncl : null,
            SeedsConnected = J.Long(props, "seeds") is >= 0 and var sc ? (int)sc : null,
            SeedsTotal = J.Long(props, "seeds_total") is >= 0 and var st ? (int)st : null,
            PeersConnected = J.Long(props, "peers") is >= 0 and var pc ? (int)pc : null,
            PeersTotal = J.Long(props, "peers_total") is >= 0 and var pt ? (int)pt : null,
            DownloadLimit = Math.Max(0, J.Long(props, "dl_limit") ?? J.Long(info, "dl_limit") ?? 0),
            UploadLimit = Math.Max(0, J.Long(props, "up_limit") ?? J.Long(info, "up_limit") ?? 0),
            AverageDownloadRate = Positive(J.Long(props, "dl_speed_avg")),
            AverageUploadRate = Positive(J.Long(props, "up_speed_avg")),
            LastActivity = Unix(J.Long(info, "last_activity")),
            LastSeenComplete = Unix(J.Long(props, "last_seen") ?? J.Long(info, "seen_complete")),
            NextAnnounceSeconds = Positive(J.Long(props, "reannounce")),
            ContentPath = J.Str(info, "content_path"),
            Tags = tags,
            WebSeeds = webSeeds is { ValueKind: System.Text.Json.JsonValueKind.Array } ws ? ws.EnumerateArray().Select(w => J.Str(w, "url")).OfType<string>().ToList() : [],
            Files = fileList,
            Peers = peerList.OrderByDescending(p => p.DownloadRate + p.UploadRate).ThenByDescending(p => p.Progress).ToList(),
            Trackers = trackerList,
            Pieces = pieceMap,
        };
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is QBittorrentException or System.Text.Json.JsonException)
        {
            return null; // optional endpoints (older qBittorrent versions)
        }
    }

    /// <summary>Tolerant JSON readers: qBittorrent field types differ between versions.</summary>
    private static class J
    {
        private static System.Text.Json.JsonElement? Get(System.Text.Json.JsonElement e, string name) =>
            e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

        public static string? Str(System.Text.Json.JsonElement e, string name) => Get(e, name) is { } v && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString())
            : null;

        public static long? Long(System.Text.Json.JsonElement e, string name) => Get(e, name) is { } v && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? (v.TryGetInt64(out var l) ? l : (long)v.GetDouble())
            : null;

        public static double? Double(System.Text.Json.JsonElement e, string name) => Get(e, name) is { } v && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;

        public static bool? Bool(System.Text.Json.JsonElement e, string name) => Get(e, name) is { } v
            ? v.ValueKind switch { System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false, _ => null }
            : null;
    }

    /// <summary>Nothing to do: the sync reads the Imported flag from the database before applying the seeding policy.</summary>
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
        SeriesId = row.SeriesId,
        Imported = row.Imported,
        ImportMessage = row.ImportMessage,
        ErrorMessage = row.ErrorMessage,
        Client = DownloadClients.QBittorrent,
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
            if (_api is null || current!.Url != settings.Url || current.Username != settings.Username || current.Password != settings.Password
                || current.ApiKey != settings.ApiKey)
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
