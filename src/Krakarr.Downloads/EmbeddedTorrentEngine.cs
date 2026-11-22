using System.Collections.Concurrent;
using System.Net;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Krakarr.Downloads;

/// <summary>
/// Hosts the embedded MonoTorrent engine and keeps its Download rows in sync with it.
/// The engine runs while "Built-in" is the selected download client or while it still owns downloads.
/// </summary>
public sealed class EmbeddedTorrentEngine(
    IServiceScopeFactory scopeFactory,
    KrakarrPaths paths,
    SettingsCache settingsCache,
    DownloadStatusFeed statusFeed,
    ILogger<EmbeddedTorrentEngine> logger) : BackgroundService, IDownloadClientImpl
{
    private readonly ConcurrentDictionary<string, TorrentManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Download> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _uploadBaseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _downloadBaseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientEngine? _engine;
    private DownloadClientSettings _settings = new();
    private volatile bool _settingsDirty;

    private ClientEngine Engine => _engine ?? throw new InvalidOperationException("Torrent engine is not started");

    /// <summary>How often a running engine checks whether it is still needed (selected or owning downloads).</summary>
    private const int ActiveCheckTicks = 30;

    public DownloadClientKind Kind => DownloadClientKind.Embedded;

    public string Name => DownloadClients.Embedded;

    public bool IsActive => _engine is not null;

    public string? LastError { get; private set; }

    public Task<DownloadClientTestResult> TestAsync(DownloadClientSettings settings, CancellationToken ct = default) =>
        Task.FromResult(new DownloadClientTestResult(true, "MonoTorrent (built-in)", null));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsCache.Changed += key =>
        {
            if (key == DownloadClientSettings.SectionKey) _settingsDirty = true;
        };

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await ShouldRunAsync(stoppingToken))
                {
                    LastError = null;
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                try
                {
                    await StartEngineAsync(stoppingToken);
                    LastError = null;
                    _ready.TrySetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Failed to start torrent engine");
                    LastError = ex.Message;
                    await ShutdownEngineAsync();
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await RunAsync(stoppingToken);
                await ShutdownEngineAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Runs the engine until the host stops, or another client is selected and this one owns no downloads.</summary>
    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            tick++;
            try
            {
                var settingsChanged = _settingsDirty;
                if (settingsChanged || tick % ActiveCheckTicks == 0)
                {
                    _settingsDirty = false;
                    if (!await ShouldRunAsync(stoppingToken))
                    {
                        logger.LogInformation("Another download client is selected and the built-in one has no torrents left; stopping it");
                        return;
                    }

                    if (settingsChanged) await ApplySettingsAsync(stoppingToken);
                }

                var statuses = GetStatuses();
                await statusFeed.PublishAsync(Kind, statuses, stoppingToken);

                // Persist state every 5 seconds.
                if (tick % 5 == 0)
                {
                    await PersistAsync(statuses, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Torrent engine tick failed");
            }
        }
    }

    /// <summary>The engine runs while it is the selected client or still owns downloads.</summary>
    private async Task<bool> ShouldRunAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<DownloadClientSettings>(ct);
        return settings.Client == DownloadClientKind.Embedded
            || await scope.ServiceProvider.GetRequiredService<KrakarrDbContext>().Downloads.AnyAsync(d => d.Client == DownloadClients.Embedded, ct);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await ShutdownEngineAsync();
    }

    private async Task ShutdownEngineAsync()
    {
        if (_engine is null)
        {
            return;
        }

        try
        {
            await PersistAsync(GetStatuses(), CancellationToken.None);
            await _engine.StopAllAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while stopping torrent engine");
        }

        _engine.Dispose();
        _engine = null;
        _managers.Clear();
        _rows.Clear();
        _uploadBaseline.Clear();
        _downloadBaseline.Clear();
        _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await statusFeed.PublishAsync(Kind, [], CancellationToken.None);
    }

    private async Task StartEngineAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        _settings = await settingsService.GetAsync<DownloadClientSettings>(ct);

        _engine = new ClientEngine(BuildEngineSettings(_settings));
        logger.LogInformation("Torrent engine listening on port {Port}", _settings.ListenPort);

        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var rows = await db.Downloads.AsNoTracking().Where(d => d.Client == DownloadClients.Embedded).ToListAsync(ct);
        foreach (var row in rows)
        {
            try
            {
                await LoadIntoEngineAsync(row, start: !row.Paused && row.State != DownloadState.Completed);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not restore torrent {Name}", row.Name);
            }
        }
    }

    private EngineSettings BuildEngineSettings(DownloadClientSettings s)
    {
        var port = s.ListenPort is > 0 and < 65536 ? s.ListenPort : 6881;
        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = paths.TorrentCacheDirectory,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AutoSaveLoadDhtCache = true,
            AllowPortForwarding = s.EnablePortForwarding,
            MaximumConnections = s.MaxConnections > 0 ? s.MaxConnections : 200,
            MaximumDownloadRate = Math.Max(0, s.MaxDownloadRateKiB) * 1024,
            MaximumUploadRate = Math.Max(0, s.MaxUploadRateKiB) * 1024,
            ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                ["ipv4"] = new(IPAddress.Any, port),
            },
            DhtEndPoint = s.EnableDht ? new IPEndPoint(IPAddress.Any, port) : null,
        };
        return builder.ToSettings();
    }

    private async Task ApplySettingsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        _settings = await scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<DownloadClientSettings>(ct);
        await Engine.UpdateSettingsAsync(BuildEngineSettings(_settings));
        logger.LogInformation("Applied new download client settings");
    }

    private string DefaultSavePath => string.IsNullOrWhiteSpace(_settings.DownloadDirectory)
        ? paths.DefaultDownloadDirectory
        : _settings.DownloadDirectory;

    public async Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default)
    {
        if (!MagnetLink.TryParse(magnetUri, out var magnet) || magnet is null)
        {
            throw new ArgumentException("Invalid magnet link", nameof(magnetUri));
        }

        var hash = magnet.InfoHashes.V1OrV2.ToHex().ToLowerInvariant();
        var row = NewRow(hash, magnet.Name ?? options.DisplayName ?? hash, options);
        row.MagnetUri = magnetUri;
        row.Size = magnet.Size ?? 0;
        return await AddRowAsync(row, ct);
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
        var filePath = Path.Combine(paths.TorrentFilesDirectory, hash + ".torrent");
        await File.WriteAllBytesAsync(filePath, torrentData, ct);

        var row = NewRow(hash, torrent.Name, options);
        row.TorrentFilePath = filePath;
        row.Size = torrent.Size;
        return await AddRowAsync(row, ct);
    }

    private Download NewRow(string hash, string name, AddTorrentOptions o) => new()
    {
        Client = DownloadClients.Embedded,
        InfoHash = hash,
        Name = name,
        Category = string.IsNullOrWhiteSpace(o.Category) ? "default" : o.Category,
        SavePath = string.IsNullOrWhiteSpace(o.SavePath) ? Path.Combine(DefaultSavePath, o.Category ?? "default") : o.SavePath,
        Paused = o.Paused,
        MediaType = o.MediaType,
        MovieId = o.MovieId,
        SeriesId = o.SeriesId,
        EpisodeIds = o.EpisodeIds.ToList(),
        QualityId = o.QualityId,
        IndexerName = o.IndexerName,
    };

    private async Task<Download> AddRowAsync(Download row, CancellationToken ct)
    {
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await _lock.WaitAsync(ct);
        try
        {
            if (_rows.TryGetValue(row.InfoHash, out var existing))
            {
                throw new InvalidOperationException($"Torrent already exists: {existing.Name}");
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            if (await db.Downloads.AnyAsync(d => d.InfoHash == row.InfoHash, ct))
            {
                throw new InvalidOperationException("Torrent already exists");
            }

            Directory.CreateDirectory(row.SavePath);
            db.Downloads.Add(row);
            await db.SaveChangesAsync(ct);

            await LoadIntoEngineAsync(row, start: !row.Paused);
            logger.LogInformation("Added torrent {Name} ({Hash})", row.Name, row.InfoHash);
            return row;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadIntoEngineAsync(Download row, bool start)
    {
        var torrentSettings = new TorrentSettingsBuilder
        {
            AllowDht = _settings.EnableDht,
            AllowPeerExchange = true,
            CreateContainingDirectory = true,
        }.ToSettings();

        TorrentManager manager;
        if (row.TorrentFilePath is not null && File.Exists(row.TorrentFilePath))
        {
            var torrent = await Torrent.LoadAsync(row.TorrentFilePath);
            manager = await Engine.AddAsync(torrent, row.SavePath, torrentSettings);
        }
        else if (row.MagnetUri is not null)
        {
            manager = await Engine.AddAsync(MagnetLink.Parse(row.MagnetUri), row.SavePath, torrentSettings);
        }
        else
        {
            throw new InvalidOperationException($"Download {row.Name} has neither a torrent file nor a magnet link");
        }

        _managers[row.InfoHash] = manager;
        _rows[row.InfoHash] = row;
        _uploadBaseline[row.InfoHash] = row.UploadedBytes;
        _downloadBaseline[row.InfoHash] = row.DownloadedBytes;

        if (start)
        {
            await manager.StartAsync();
        }
    }

    public async Task PauseAsync(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var manager)) throw new KeyNotFoundException(infoHash);
        await manager.StopAsync();
        await UpdateRowAsync(infoHash, r => r.Paused = true);
    }

    public async Task ResumeAsync(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var manager)) throw new KeyNotFoundException(infoHash);
        await UpdateRowAsync(infoHash, r =>
        {
            r.Paused = false;
            if (r.State == DownloadState.Completed) r.State = DownloadState.Seeding;
        });
        await manager.StartAsync();
    }

    public async Task RemoveAsync(string infoHash, bool deleteData)
    {
        await _lock.WaitAsync();
        try
        {
            if (_managers.TryRemove(infoHash, out var manager))
            {
                if (manager.State != TorrentState.Stopped)
                {
                    await manager.StopAsync(TimeSpan.FromSeconds(10));
                }

                await Engine.RemoveAsync(manager, deleteData ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
            }

            _rows.TryRemove(infoHash, out var row);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
            await db.Downloads.Where(d => d.InfoHash == infoHash && d.Client == DownloadClients.Embedded).ExecuteDeleteAsync();

            if (row?.TorrentFilePath is { } file && File.Exists(file))
            {
                File.Delete(file);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(infoHash, out var manager) || !manager.HasMetadata)
        {
            return Task.FromResult<IReadOnlyList<(string Path, long Length)>>([]);
        }

        return Task.FromResult<IReadOnlyList<(string Path, long Length)>>(manager.Files.Select(f => (f.FullPath, f.Length)).ToList());
    }

    public IReadOnlyList<TorrentStatus> GetStatuses()
    {
        var list = new List<TorrentStatus>(_managers.Count);
        foreach (var (hash, manager) in _managers)
        {
            if (!_rows.TryGetValue(hash, out var row)) continue;
            list.Add(BuildStatus(row, manager));
        }

        return list.OrderByDescending(s => s.Added).ToList();
    }

    private TorrentStatus BuildStatus(Download row, TorrentManager manager)
    {
        var size = manager.HasMetadata ? manager.Torrent!.Size : row.Size;
        var uploaded = _uploadBaseline.GetValueOrDefault(row.InfoHash) + manager.Monitor.DataBytesSent;
        var downloaded = _downloadBaseline.GetValueOrDefault(row.InfoHash) + manager.Monitor.DataBytesReceived;
        var progress = manager.HasMetadata ? manager.Progress : 0;
        var remaining = (long)(size * (1 - progress / 100.0));
        var rate = manager.Monitor.DownloadRate;

        var state = MapState(manager, row);
        return new TorrentStatus
        {
            Id = row.Id,
            InfoHash = row.InfoHash,
            Name = manager.HasMetadata ? manager.Torrent!.Name : row.Name,
            State = state,
            Progress = Math.Round(progress, 2),
            Size = size,
            Downloaded = downloaded,
            Uploaded = uploaded,
            DownloadRate = rate,
            UploadRate = manager.Monitor.UploadRate,
            Seeds = manager.Peers.Seeds,
            Leechers = manager.Peers.Leechs,
            Ratio = size > 0 ? Math.Round((double)uploaded / size, 3) : 0,
            EtaSeconds = state == DownloadState.Downloading && rate > 0 ? remaining / rate : null,
            Category = row.Category,
            SavePath = row.SavePath,
            Added = row.Added,
            CompletedAt = row.CompletedAt,
            MovieId = row.MovieId,
            SeriesId = row.SeriesId,
            Imported = row.Imported,
            ImportMessage = row.ImportMessage,
            ErrorMessage = manager.Error?.Exception?.Message,
            Client = DownloadClients.Embedded,
        };
    }

    private static DownloadState MapState(TorrentManager manager, Download row) => manager.State switch
    {
        TorrentState.Metadata => DownloadState.FetchingMetadata,
        TorrentState.Downloading => DownloadState.Downloading,
        TorrentState.Seeding => DownloadState.Seeding,
        TorrentState.Error => DownloadState.Error,
        TorrentState.Paused or TorrentState.HashingPaused => DownloadState.Paused,
        TorrentState.Stopped or TorrentState.Stopping when manager.Complete && !row.Paused => DownloadState.Completed,
        TorrentState.Stopped or TorrentState.Stopping => row.Paused ? DownloadState.Paused : DownloadState.Queued,
        _ => manager.Complete ? DownloadState.Seeding : DownloadState.Queued,
    };

    private async Task PersistAsync(IReadOnlyList<TorrentStatus> statuses, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var rows = await db.Downloads.Where(d => d.Client == DownloadClients.Embedded)
            .ToDictionaryAsync(d => d.InfoHash, StringComparer.OrdinalIgnoreCase, ct);
        var completedNow = new List<Download>();
        var toRemove = new List<(string InfoHash, string Name, bool DeleteData)>();

        foreach (var status in statuses)
        {
            if (!rows.TryGetValue(status.InfoHash, out var row)) continue;
            _managers.TryGetValue(status.InfoHash, out var manager);

            row.Name = status.Name;
            row.Size = status.Size;
            row.UploadedBytes = status.Uploaded;
            row.DownloadedBytes = status.Downloaded;
            row.ErrorMessage = manager?.Error?.Exception?.Message;

            if (manager is not null && manager.Complete && row.CompletedAt is null)
            {
                row.CompletedAt = DateTimeOffset.UtcNow;
                completedNow.Add(row);
                logger.LogInformation("Download completed: {Name}", row.Name);
            }

            row.State = status.State;

            // Seeding policy: once the ratio/time limit is reached and the media (if any) is imported,
            // remove the torrent or stop it, depending on the settings.
            var seedingDone = manager is not null && !row.Paused
                && status.State is DownloadState.Seeding or DownloadState.Completed
                && SeedingPolicy.IsDone(_settings, row, status.Ratio, DateTimeOffset.UtcNow);
            if (seedingDone && _settings.SeedLimitAction == SeedLimitAction.Remove)
            {
                toRemove.Add((row.InfoHash, row.Name, SeedingPolicy.DeleteDataOnRemove(row)));
            }
            else if (seedingDone && status.State == DownloadState.Seeding)
            {
                logger.LogInformation("Seeding limit reached for {Name}, stopping", row.Name);
                await manager!.StopAsync();
                row.State = DownloadState.Completed;
            }

            if (_rows.TryGetValue(row.InfoHash, out var cached))
            {
                cached.Name = row.Name;
                cached.State = row.State;
                cached.CompletedAt = row.CompletedAt;
                cached.Size = row.Size;
                cached.Imported = row.Imported;
                cached.ImportedAt = row.ImportedAt;
                cached.Paused = row.Paused;
                cached.ImportMessage = row.ImportMessage;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var (infoHash, name, deleteData) in toRemove)
        {
            logger.LogInformation("Seeding limit reached for {Name}, removing it{Data}", name, deleteData ? " and its data" : string.Empty);
            await RemoveAsync(infoHash, deleteData);
        }
    }

    private async Task UpdateRowAsync(string infoHash, Action<Download> update)
    {
        if (_rows.TryGetValue(infoHash, out var cached))
        {
            update(cached);
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var row = await db.Downloads.FirstOrDefaultAsync(d => d.InfoHash == infoHash && d.Client == DownloadClients.Embedded);
        if (row is not null)
        {
            update(row);
            await db.SaveChangesAsync();
        }
    }

    public async Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default)
    {
        if (!_managers.TryGetValue(infoHash, out var manager) || !_rows.TryGetValue(infoHash, out var row))
        {
            return null;
        }

        var status = BuildStatus(row, manager);
        var torrent = manager.HasMetadata ? manager.Torrent : null;

        var files = manager.HasMetadata
            ? manager.Files.Select((f, i) => new TorrentFileDetails(
                i, f.Path, f.Length, Math.Round(f.BitField.PercentComplete, 2), PriorityName(f.Priority), null)).ToList()
            : [];

        var peers = new List<TorrentPeerDetails>();
        foreach (var peer in await manager.GetPeersAsync())
        {
            var flags = new List<string>();
            var desc = new List<string>();
            if (peer.IsInterested && !peer.AmChoking) { flags.Add("U"); desc.Add("U = uploading to peer"); }
            if (peer.AmInterested && !peer.IsChoking) { flags.Add("D"); desc.Add("D = downloading from peer"); }
            if (peer.AmInterested && peer.IsChoking) { flags.Add("d"); desc.Add("d = interested, peer is choking"); }
            if (peer.ConnectionDirection == MonoTorrent.Direction.Incoming) { flags.Add("I"); desc.Add("I = incoming connection"); }
            if (peer.EncryptionType != MonoTorrent.Connections.EncryptionType.PlainText) { flags.Add("E"); desc.Add("E = encrypted"); }
            peers.Add(new TorrentPeerDetails
            {
                Address = peer.Uri is { } uri ? $"{uri.Host}:{uri.Port}" : "unknown",
                Client = PeerClients.Describe(peer.ClientApp.ShortId ?? peer.ClientApp.ToString()),
                Progress = Math.Round(peer.BitField.PercentComplete, 1),
                DownloadRate = peer.Monitor.DownloadRate,
                UploadRate = peer.Monitor.UploadRate,
                Downloaded = peer.Monitor.DataBytesReceived,
                Uploaded = peer.Monitor.DataBytesSent,
                Flags = string.Join(' ', flags),
                FlagsDescription = string.Join(", ", desc),
                Connection = (peer.Uri?.Scheme is "ipv4" or "ipv6" or null ? "BT" : peer.Uri.Scheme.ToUpperInvariant())
                    + peer.EncryptionType switch
                    {
                        MonoTorrent.Connections.EncryptionType.RC4Full => " · Encrypted (RC4)",
                        MonoTorrent.Connections.EncryptionType.RC4Header => " · Encrypted (RC4 header)",
                        _ => string.Empty,
                    },
                IsSeeder = peer.IsSeeder,
            });
        }

        var trackers = new List<TorrentTrackerDetails>
        {
            new() { Url = "** [DHT] **", Status = manager.CanUseDht ? "Working" : "Disabled" },
            new() { Url = "** [PeX] **", Status = manager.Settings.AllowPeerExchange && !(torrent?.IsPrivate ?? false) ? "Working" : "Disabled" },
            new() { Url = "** [LSD] **", Status = manager.CanUseLocalPeerDiscovery ? "Working" : "Disabled" },
        };
        var tierIndex = 0;
        foreach (var tier in manager.TrackerManager.Tiers)
        {
            MonoTorrent.Trackers.ScrapeInfo? scrape = tier.ScrapeInfo.Count > 0 ? tier.ScrapeInfo.Values.First() : null;
            foreach (var tracker in tier.Trackers)
            {
                var active = ReferenceEquals(tier.ActiveTracker, tracker);
                trackers.Add(new TorrentTrackerDetails
                {
                    Url = tracker.Uri.ToString(),
                    Tier = tierIndex,
                    Status = tracker.Status switch
                    {
                        MonoTorrent.Trackers.TrackerState.Ok => "Working",
                        MonoTorrent.Trackers.TrackerState.Connecting => "Updating",
                        MonoTorrent.Trackers.TrackerState.Offline => "Not working",
                        MonoTorrent.Trackers.TrackerState.InvalidResponse => "Not working",
                        _ => "Not contacted",
                    },
                    Seeds = active ? scrape?.Complete : null,
                    Leechers = active ? scrape?.Incomplete : null,
                    Downloaded = active ? scrape?.Downloaded : null,
                    Message = !string.IsNullOrEmpty(tracker.FailureMessage) ? tracker.FailureMessage : string.IsNullOrEmpty(tracker.WarningMessage) ? null : tracker.WarningMessage,
                    NextAnnounceSeconds = active && tracker.UpdateInterval > TimeSpan.Zero
                        ? (long)Math.Max(0, (tracker.UpdateInterval - tracker.TimeSinceLastAnnounce).TotalSeconds)
                        : null,
                });
            }

            tierIndex++;
        }

        string? pieces = null;
        if (manager.HasMetadata)
        {
            var bitfield = manager.Bitfield;
            var states = new int[bitfield.Length];
            for (var i = 0; i < states.Length; i++) states[i] = bitfield[i] ? 2 : 0;
            pieces = PieceMap.Build(states);
        }

        var running = manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata;
        var v1 = manager.InfoHashes.V1?.ToHex().ToLowerInvariant();
        var v2 = manager.InfoHashes.V2?.ToHex().ToLowerInvariant();
        return new TorrentDetails
        {
            Status = status,
            Client = "Built-in (MonoTorrent)",
            ClientState = manager.State.ToString(),
            InfoHashV1 = v1,
            InfoHashV2 = v2,
            MagnetUri = row.MagnetUri ?? BuildMagnet(v1, status.Name, torrent),
            Comment = string.IsNullOrWhiteSpace(torrent?.Comment) ? null : torrent.Comment,
            CreatedBy = string.IsNullOrWhiteSpace(torrent?.CreatedBy) ? null : torrent.CreatedBy,
            CreationDate = torrent is { CreationDate.Year: > 1970 } ? new DateTimeOffset(DateTime.SpecifyKind(torrent.CreationDate, DateTimeKind.Utc)) : null,
            IsPrivate = torrent?.IsPrivate,
            PieceSize = torrent?.PieceLength,
            PieceCount = torrent?.PieceCount,
            PiecesHave = manager.HasMetadata ? manager.Bitfield.TrueCount : null,
            TotalSize = torrent?.Size,
            SelectedSize = manager.HasMetadata ? manager.Files.Where(f => f.Priority != MonoTorrent.Priority.DoNotDownload).Sum(f => f.Length) : null,
            SessionDownloaded = manager.Monitor.DataBytesReceived,
            SessionUploaded = manager.Monitor.DataBytesSent,
            Wasted = manager.HashFails * (long)(torrent?.PieceLength ?? 0),
            TimeActiveSeconds = running && manager.StartTime > DateTime.MinValue ? (long)(DateTime.Now - manager.StartTime).TotalSeconds : null,
            SeedingTimeSeconds = row.CompletedAt is { } done && status.State == DownloadState.Seeding ? (long)(DateTimeOffset.UtcNow - done).TotalSeconds : null,
            Connections = manager.OpenConnections,
            ConnectionsLimit = manager.Settings.MaximumConnections,
            SeedsConnected = manager.Peers.Seeds,
            PeersConnected = manager.Peers.Leechs,
            PeersTotal = manager.Peers.Available,
            DownloadLimit = manager.Settings.MaximumDownloadRate,
            UploadLimit = manager.Settings.MaximumUploadRate,
            ContentPath = manager.HasMetadata && manager.Files.Count == 1 ? manager.Files[0].FullPath : manager.ContainingDirectory,
            Tags = [row.Category],
            WebSeeds = torrent?.HttpSeeds.Select(u => u.ToString()).ToList() ?? [],
            Files = files,
            Peers = peers.OrderByDescending(p => p.DownloadRate + p.UploadRate).ThenByDescending(p => p.Progress).ToList(),
            Trackers = trackers,
            Pieces = pieces,
        };
    }

    private static string PriorityName(MonoTorrent.Priority p) => p switch
    {
        MonoTorrent.Priority.DoNotDownload => "Skip",
        MonoTorrent.Priority.Lowest or MonoTorrent.Priority.Low => "Low",
        MonoTorrent.Priority.Normal => "Normal",
        MonoTorrent.Priority.High => "High",
        _ => "Maximum",
    };

    private static string? BuildMagnet(string? v1, string name, Torrent? torrent)
    {
        if (v1 is null) return null;
        var trackers = torrent?.AnnounceUrls.SelectMany(t => t).Select(u => "&tr=" + Uri.EscapeDataString(u)) ?? [];
        return $"magnet:?xt=urn:btih:{v1}&dn={Uri.EscapeDataString(name)}{string.Concat(trackers)}";
    }

    public void MarkImported(string infoHash)
    {
        if (_rows.TryGetValue(infoHash, out var cached))
        {
            cached.Imported = true;
            cached.ImportedAt = DateTimeOffset.UtcNow;
        }
    }
}
