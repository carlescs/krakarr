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

public sealed record DownloadClientTestResult(bool Success, string? Version, string? Message);

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
}
