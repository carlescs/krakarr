namespace Krakarr.Core.Configuration;

/// <summary>Well-known filesystem locations, resolved at startup.</summary>
public sealed class KrakarrPaths
{
    public required string ConfigDirectory { get; init; }
    public required string DefaultDownloadDirectory { get; init; }

    public string TorrentCacheDirectory => Path.Combine(ConfigDirectory, "torrent-cache");
    public string TorrentFilesDirectory => Path.Combine(ConfigDirectory, "torrents");
    public string LogsDirectory => Path.Combine(ConfigDirectory, "logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(TorrentCacheDirectory);
        Directory.CreateDirectory(TorrentFilesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        try
        {
            Directory.CreateDirectory(DefaultDownloadDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            // Download dir may be a mount we can't create; surfaced later when used.
        }
    }
}
