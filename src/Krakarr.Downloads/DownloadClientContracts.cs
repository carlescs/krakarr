using Krakarr.Core.Configuration;
using Krakarr.Core.Domain;
using Krakarr.Data;
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

/// <summary>The download client used by the rest of Krakarr (whichever one is currently selected).</summary>
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
    /// <summary>Last connection/startup error, or null when healthy.</summary>
    string? LastError { get; }
    Task<DownloadClientTestResult> TestAsync(DownloadClientSettings settings, CancellationToken ct = default);
}

/// <summary>Routes every call to the download client selected in Settings → Download client.</summary>
public sealed class DownloadClientRouter(
    IEnumerable<IDownloadClientImpl> clients,
    SettingsCache settingsCache,
    IServiceScopeFactory scopeFactory) : ITorrentEngine
{
    private readonly IReadOnlyDictionary<DownloadClientKind, IDownloadClientImpl> _clients = clients.ToDictionary(c => c.Kind);

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

    public IDownloadClientImpl Current => Get(CurrentSettings.Client);

    public IDownloadClientImpl Get(DownloadClientKind kind) =>
        _clients.TryGetValue(kind, out var client) ? client : throw new InvalidOperationException($"Download client {kind} is not available");

    public Task<Download> AddMagnetAsync(string magnetUri, AddTorrentOptions options, CancellationToken ct = default) => Current.AddMagnetAsync(magnetUri, options, ct);
    public Task<Download> AddTorrentAsync(byte[] torrentData, AddTorrentOptions options, CancellationToken ct = default) => Current.AddTorrentAsync(torrentData, options, ct);
    public Task PauseAsync(string infoHash) => Current.PauseAsync(infoHash);
    public Task ResumeAsync(string infoHash) => Current.ResumeAsync(infoHash);
    public Task RemoveAsync(string infoHash, bool deleteData) => Current.RemoveAsync(infoHash, deleteData);
    public IReadOnlyList<TorrentStatus> GetStatuses() => Current.GetStatuses();
    public Task<IReadOnlyList<(string Path, long Length)>> GetFilesAsync(string infoHash, CancellationToken ct = default) => Current.GetFilesAsync(infoHash, ct);
    public void MarkImported(string infoHash) => Current.MarkImported(infoHash);
    public Task<TorrentDetails?> GetDetailsAsync(string infoHash, CancellationToken ct = default) => Current.GetDetailsAsync(infoHash, ct);
}
