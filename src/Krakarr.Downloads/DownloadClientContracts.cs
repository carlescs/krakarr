using System.Collections.Concurrent;
using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Core.Events;
using Krakarr.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Krakarr.Downloads;

public sealed record AddTorrentOptions
{
    public string Category { get; init; } = "default";
    public string? SavePath { get; init; }
    public bool Paused { get; init; }
    public MediaType? MediaType { get; init; }
    public int? MovieId { get; init; }
    public int? SeriesId { get; init; }
    public IReadOnlyList<int> EpisodeIds { get; init; } = [];
    public int? QualityId { get; init; }
    public string? IndexerName { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>
/// Thrown by a client when Krakarr already has a download row for the torrent but the client itself isn't running it.
/// <see cref="DownloadClientRouter"/> decides whether the row is stale (no client has the torrent) and retries.
/// </summary>
public sealed class StaleDownloadException(string infoHash) : InvalidOperationException("Torrent already exists")
{
    public string InfoHash { get; } = infoHash;
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
    public int? SeriesId { get; init; }
    public bool Imported { get; init; }
    public string? ImportMessage { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>Which client owns the torrent (<see cref="DownloadClients"/>).</summary>
    public string Client { get; init; } = DownloadClients.Embedded;
}

public sealed record DownloadClientTestResult(bool Success, string? Version, string? Message);

/// <summary>Everything a client can tell about one torrent. Fields a client does not know are null.</summary>
public sealed record TorrentDetails
{
    public required TorrentStatus Status { get; init; }
    /// <summary>Client name and version, e.g. "qBittorrent v4.6.3" or "Built-in (MonoTorrent)".</summary>
    public required string Client { get; init; }
    /// <summary>The client's own state name (e.g. "stalledUP", "Seeding").</summary>
    public string? ClientState { get; init; }
    public string? InfoHashV1 { get; init; }
    public string? InfoHashV2 { get; init; }
    public string? MagnetUri { get; init; }
    public string? Comment { get; init; }
    public string? CreatedBy { get; init; }
    public DateTimeOffset? CreationDate { get; init; }
    public bool? IsPrivate { get; init; }
    public long? PieceSize { get; init; }
    public int? PieceCount { get; init; }
    public int? PiecesHave { get; init; }
    /// <summary>Size of all files in the torrent (selected or not).</summary>
    public long? TotalSize { get; init; }
    /// <summary>Size of the files selected for download.</summary>
    public long? SelectedSize { get; init; }
    public long? SessionDownloaded { get; init; }
    public long? SessionUploaded { get; init; }
    public long? Wasted { get; init; }
    /// <summary>Distributed copies available in the swarm.</summary>
    public double? Availability { get; init; }
    public long? TimeActiveSeconds { get; init; }
    public long? SeedingTimeSeconds { get; init; }
    public int? Connections { get; init; }
    public int? ConnectionsLimit { get; init; }
    public int? SeedsConnected { get; init; }
    public int? SeedsTotal { get; init; }
    public int? PeersConnected { get; init; }
    public int? PeersTotal { get; init; }
    /// <summary>Bytes/s; 0 = unlimited.</summary>
    public long? DownloadLimit { get; init; }
    public long? UploadLimit { get; init; }
    public long? AverageDownloadRate { get; init; }
    public long? AverageUploadRate { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public DateTimeOffset? LastSeenComplete { get; init; }
    public long? NextAnnounceSeconds { get; init; }
    public string? ContentPath { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> WebSeeds { get; init; } = [];
    public IReadOnlyList<TorrentFileDetails> Files { get; init; } = [];
    public IReadOnlyList<TorrentPeerDetails> Peers { get; init; } = [];
    public IReadOnlyList<TorrentTrackerDetails> Trackers { get; init; } = [];
    /// <summary>Piece map squeezed into at most <see cref="PieceMap.MaxBuckets"/> chars: '0' missing, '1' partial/downloading, '2' complete.</summary>
    public string? Pieces { get; init; }
}

public sealed record TorrentFileDetails(int Index, string Path, long Size, double Progress, string Priority, double? Availability);

public sealed record TorrentPeerDetails
{
    public required string Address { get; init; }
    public string? Client { get; init; }
    /// <summary>0-100.</summary>
    public double Progress { get; init; }
    public long DownloadRate { get; init; }
    public long UploadRate { get; init; }
    public long Downloaded { get; init; }
    public long Uploaded { get; init; }
    /// <summary>Compact flags (qBittorrent style, e.g. "D U E I").</summary>
    public string? Flags { get; init; }
    public string? FlagsDescription { get; init; }
    /// <summary>Transport, e.g. "BT", "μTP", "Encrypted (RC4)".</summary>
    public string? Connection { get; init; }
    public string? Country { get; init; }
    public bool? IsSeeder { get; init; }
}

public sealed record TorrentTrackerDetails
{
    public required string Url { get; init; }
    public int Tier { get; init; }
    public required string Status { get; init; }
    public int? Seeds { get; init; }
    public int? Leechers { get; init; }
    public int? Peers { get; init; }
    public int? Downloaded { get; init; }
    public string? Message { get; init; }
    public long? NextAnnounceSeconds { get; init; }
}

/// <summary>Downsamples per-piece states for a compact progress bar.</summary>
public static class PieceMap
{
    public const int MaxBuckets = 300;

    /// <param name="states">Per piece: 0 missing, 1 downloading/partial, 2 complete.</param>
    public static string Build(IReadOnlyList<int> states)
    {
        if (states.Count == 0) return string.Empty;
        var buckets = Math.Min(states.Count, MaxBuckets);
        var chars = new char[buckets];
        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * states.Count / buckets);
            var end = (int)((long)(b + 1) * states.Count / buckets);
            int complete = 0, any = 0;
            for (var i = start; i < end; i++)
            {
                if (states[i] == 2) complete++;
                if (states[i] > 0) any++;
            }

            chars[b] = complete == end - start ? '2' : any > 0 ? '1' : '0';
        }

        return new string(chars);
    }
}

/// <summary>
/// The download clients as seen by the rest of Krakarr: new torrents go to the client selected in settings, and every
/// other call goes to the client that owns the torrent.
/// </summary>
public interface ITorrentEngine
{
    Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default);
    Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default);
    Task PauseAsync(string infoHash);
    Task ResumeAsync(string infoHash);
    Task RemoveAsync(string infoHash, bool deleteData);
    IReadOnlyList<TorrentStatus> GetStatuses();
    /// <summary>Absolute (local) paths of the files in a torrent, or empty if unknown/not yet available.</summary>
    Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default);
    /// <summary>Marks a download as imported so a seeding policy can apply.</summary>
    void MarkImported(string infoHash);
    /// <summary>Full details (files, peers, trackers, pieces) or null when the torrent is unknown.</summary>
    Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default);
}

/// <summary>A concrete download client implementation.</summary>
public interface IDownloadClientImpl : ITorrentEngine
{
    DownloadClientKind Kind { get; }
    /// <summary>The <see cref="Download.Client"/> value of the torrents this client owns.</summary>
    string Name { get; }
    /// <summary>
    /// True while the client runs: it is the selected client, or it still owns downloads (for example torrents left
    /// seeding after switching to another client).
    /// </summary>
    bool IsActive { get; }
    /// <summary>Last connection/startup error, or null when healthy.</summary>
    string? LastError { get; }
    Task<DownloadClientTestResult> TestAsync(DownloadClientSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Adds new torrents to the download client selected in Settings → Download client, and routes every other call to
/// the client that owns the torrent (<see cref="Download.Client"/>), so switching clients doesn't strand existing
/// downloads. Statuses are merged from every client.
/// </summary>
public sealed class DownloadClientRouter(
    IEnumerable<IDownloadClientImpl> clients,
    SettingsCache settingsCache,
    IServiceScopeFactory scopeFactory) : ITorrentEngine
{
    private readonly IDownloadClientImpl[] _all = clients.ToArray();
    private readonly IReadOnlyDictionary<DownloadClientKind, IDownloadClientImpl> _clients = clients.ToDictionary(c => c.Kind);

    public IReadOnlyList<IDownloadClientImpl> All => _all;

    public DownloadClientSettings CurrentSettings
    {
        get
        {
            if (settingsCache.TryGet(DownloadClientSettings.SectionKey, out var cached))
            {
                return (DownloadClientSettings)cached;
            }

            using var scope = scopeFactory.CreateScope();
            return scope.ServiceProvider.GetRequiredService<ISettingsService>().GetAsync<DownloadClientSettings>().GetAwaiter().GetResult();
        }
    }

    /// <summary>The client new torrents are added to.</summary>
    public IDownloadClientImpl Current => Get(CurrentSettings.Client);

    public IDownloadClientImpl Get(DownloadClientKind kind) =>
        _clients.TryGetValue(kind, out var client) ? client : throw new InvalidOperationException($"Download client {kind} is not available");

    /// <summary>The client that owns a torrent; the selected client when Krakarr doesn't track it.</summary>
    public async Task<IDownloadClientImpl> OwnerAsync(string infoHash, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var hash = infoHash.ToLowerInvariant();
        var name = await db.Downloads.AsNoTracking().Where(d => d.InfoHash == hash).Select(d => d.Client).FirstOrDefaultAsync(ct);
        return Array.Find(_all, c => c.Name == name) ?? Current;
    }

    public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) =>
        AddAsync(client => client.AddMagnetAsync(magnetUri, options, ct), ct);

    public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) =>
        AddAsync(client => client.AddTorrentAsync(torrentData, options, ct), ct);

    /// <summary>
    /// Adds to the selected client. Krakarr keeps a download row after its torrent left the client (removed in
    /// qBittorrent, or a torrent the built-in engine couldn't restore), which would block grabbing the same torrent
    /// again; such a row is forgotten and the add retried once.
    /// </summary>
    private async Task<Download> AddAsync(Func<IDownloadClientImpl, Task<Download>> add, CancellationToken ct)
    {
        var client = Current;
        try
        {
            return await add(client);
        }
        catch (StaleDownloadException ex)
        {
            await ForgetStaleAsync(ex.InfoHash, ct);
            return await add(client);
        }
    }

    /// <summary>Recently added rows may not be listed by their client yet, so they are never treated as stale.</summary>
    internal static readonly TimeSpan StaleGracePeriod = TimeSpan.FromMinutes(1);

    private async Task ForgetStaleAsync(string infoHash, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KrakarrDbContext>();
        var hash = infoHash.ToLowerInvariant();
        var row = await db.Downloads.AsNoTracking().Where(d => d.InfoHash == hash)
            .Select(d => new { d.Id, d.Name, d.Added }).FirstOrDefaultAsync(ct);
        if (row is null) return; // already gone

        var live = _all.Any(c => c.GetStatuses().Any(s => string.Equals(s.InfoHash, hash, StringComparison.OrdinalIgnoreCase)));
        if (live || DateTimeOffset.UtcNow - row.Added < StaleGracePeriod)
        {
            throw new InvalidOperationException($"Torrent already exists: {row.Name}");
        }

        await db.Downloads.Where(d => d.Id == row.Id).ExecuteDeleteAsync(ct);
    }

    public async Task PauseAsync(string infoHash) => await (await OwnerAsync(infoHash)).PauseAsync(infoHash);
    public async Task ResumeAsync(string infoHash) => await (await OwnerAsync(infoHash)).ResumeAsync(infoHash);
    public async Task RemoveAsync(string infoHash, bool deleteData) => await (await OwnerAsync(infoHash)).RemoveAsync(infoHash, deleteData);

    public IReadOnlyList<TorrentStatus> GetStatuses() => Merge(_all.Select(c => c.GetStatuses()));

    public async Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default) =>
        await (await OwnerAsync(infoHash, ct)).GetFilesAsync(infoHash, ct);

    // Clients ignore torrents they don't own, so there's no need to look the owner up.
    public void MarkImported(string infoHash)
    {
        foreach (var client in _all) client.MarkImported(infoHash);
    }

    public async Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default) =>
        await (await OwnerAsync(infoHash, ct)).GetDetailsAsync(infoHash, ct);

    /// <summary>Merges per-client status lists, newest first.</summary>
    public static IReadOnlyList<TorrentStatus> Merge(IEnumerable<IReadOnlyList<TorrentStatus>> lists)
    {
        var merged = new List<TorrentStatus>();
        foreach (var list in lists) merged.AddRange(list);
        merged.Sort((a, b) => b.Added.CompareTo(a.Added));
        return merged;
    }
}

/// <summary>
/// Publishes <see cref="EventNames.DownloadsUpdated"/> with the torrents of every client. Each client reports its own
/// list here; the event carries the latest list of all of them, so one client's update doesn't hide the others.
/// </summary>
public sealed class DownloadStatusFeed(IEventHub eventHub)
{
    private readonly ConcurrentDictionary<DownloadClientKind, IReadOnlyList<TorrentStatus>> _latest = new();

    public Task PublishAsync(DownloadClientKind client, IReadOnlyList<TorrentStatus> statuses, CancellationToken ct = default)
    {
        if (statuses.Count == 0)
        {
            // Nothing to show for this client (stopped or unreachable): skip the event when it had nothing either.
            if (!_latest.TryRemove(client, out var previous) || previous.Count == 0) return Task.CompletedTask;
        }
        else
        {
            _latest[client] = statuses;
        }

        return eventHub.PublishAsync(EventNames.DownloadsUpdated, DownloadClientRouter.Merge(_latest.Values), ct);
    }
}
