using System.Collections.Concurrent;
using System.Net;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;

namespace Krakarr.Downloads;

public sealed record AddTorrentOptions
{
    public string Category { get; init; } = "default";
    public string? SavePath { get; init; }
    public bool Paused { get; init; }
    public MediaType? MediaType { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public int? QualityId { get; init; }
    public string? IndexerName { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record TorrentStatus
{
    public int Id { get; init; }
    public required string InfoHash { get; init; }
    public required string Name { get; init; }
    public DownloadState State { get; init; }
    public double Progress { get; init; }
    public long Size { get; init; }
    public long Downloaded { get; init; }
    public long Uploaded { get; init; }
    public long DownloadRate { get; init; }
    public long UploadRate { get; init; }
    public int Seeds { get; init; }
    public int Leechers { get; init; }
    public double Ratio { get; init; }
    public long? EtaSeconds { get; init; }
    public required string Category { get; init; }
    public required string SavePath { get; init; }
    public DateTimeOffset Added { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? MovieId { get; init; }
    public bool Imported { get; init; }
    public string? ImportMessage { get; init; }
    public string? ErrorMessage { get; init; }
}

public interface ITorrentEngine
{
    Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default);
    Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default);
    Task PauseAsync(string infoHash);
    Task ResumeAsync(string infoHash);
    Task RemoveAsync(string infoHash, bool deleteData);
    IReadOnlyList<TorrentStatus> GetStatuses();
    /// <summary>Absolute paths of the files in a torrent, or empty if unknown/not yet available.</summary>
    IReadOnlyList<(string Path, long Length)> GetFiles(string infoHash);
    /// <summary>Marks a download as imported so the seeding policy can apply.</summary>
    void MarkImported(string infoHash);
}

/// <summary>Hosts the embedded MonoTorrent engine and keeps the Download table in sync with it.</summary>
public sealed class TorrentEngine(
    IServiceScopeFactory scopeFactory,
    KrakarrPaths paths,
    SettingsCache settingsCache,
    IEventHub eventHub,
    ILogger<TorrentEngine> logger) : BackgroundService, ITorrentEngine
{
    private readonly ConcurrentDictionary<string, TorrentManager> _managers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Download> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _uploadBaseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _downloadBaseline = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ClientEngine? _engine;
    private DownloadClientSettings _settings = new();
    private volatile bool _settingsDirty;

    private ClientEngine Engine => _engine ?? throw new InvalidOperationException("Torrent engine is not started");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StartEngineAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start torrent engine");
            _ready.TrySetException(ex);
            return;
        }

        _ready.TrySetResult();
        settingsCache.Changed += key =>
        {
            if (key == DownloadClientSettings.SectionKey) _settingsDirty = true;
        };

        var tick = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tick++;
                try
                {
                    if (_settingsDirty)
                    {
                        _settingsDirty = false;
                        await ApplySettingsAsync(stoppingToken);
                    }

                    var statuses = GetStatuses();
                    await eventHub.PublishAsync(EventNames.DownloadsUpdated, statuses, stoppingToken);

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
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_engine is not null)
        {
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
        }
    }

    private async Task StartEngineAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        _settings = await settingsService.GetAsync<DownloadClientSettings>(ct);

        _engine = new ClientEngine(BuildEngineSettings(_settings));
        logger.LogInformation("Torrent engine listening on port {Port}", _settings.ListenPort);

        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var rows = await db.Downloads.AsNoTracking().ToListAsync(ct);
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
        InfoHash = hash,
        Name = name,
        Category = string.IsNullOrWhiteSpace(o.Category) ? "default" : o.Category,
        SavePath = string.IsNullOrWhiteSpace(o.SavePath) ? Path.Combine(DefaultSavePath, o.Category ?? "default") : o.SavePath,
        Paused = o.Paused,
        MediaType = o.MediaType,
        MovieId = o.MovieId,
        SeriesId = o.SeriesId,
        QualityId = o.QualityId,
        IndexerName = o.IndexerName,
    };

    private async Task<Download> AddRowAsync(Download row, CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ct);
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
            await db.Downloads.Where(d => d.InfoHash == infoHash).ExecuteDeleteAsync();

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

    public IReadOnlyList<(string Path, long Length)> GetFiles(string infoHash)
    {
        if (!_managers.TryGetValue(infoHash, out var manager) || !manager.HasMetadata)
        {
            return [];
        }

        return manager.Files.Select(f => (f.FullPath, f.Length)).ToList();
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
            Imported = row.Imported,
            ImportMessage = row.ImportMessage,
            ErrorMessage = manager.Error?.Exception?.Message,
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
        var rows = await db.Downloads.ToDictionaryAsync(d => d.InfoHash, StringComparer.OrdinalIgnoreCase, ct);
        var completedNow = new List<Download>();

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

            // Seeding policy: stop once ratio/time limits are reached and the media (if any) is imported.
            if (manager is not null && status.State == DownloadState.Seeding && row.CompletedAt is not null
                && (row.MovieId is null && row.SeriesId is null || row.Imported)
                && SeedingLimitReached(status, row))
            {
                logger.LogInformation("Seeding limit reached for {Name}, stopping", row.Name);
                await manager.StopAsync();
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
    }

    private bool SeedingLimitReached(TorrentStatus status, Download row)
    {
        if (_settings.SeedRatioLimit > 0 && status.Ratio >= _settings.SeedRatioLimit)
        {
            return true;
        }

        return _settings.SeedTimeLimitMinutes > 0
            && row.CompletedAt is { } completed
            && DateTimeOffset.UtcNow - completed >= TimeSpan.FromMinutes(_settings.SeedTimeLimitMinutes);
    }

    private async Task UpdateRowAsync(string infoHash, Action<Download> update)
    {
        if (_rows.TryGetValue(infoHash, out var cached))
        {
            update(cached);
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var row = await db.Downloads.FirstOrDefaultAsync(d => d.InfoHash == infoHash);
        if (row is not null)
        {
            update(row);
            await db.SaveChangesAsync();
        }
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
