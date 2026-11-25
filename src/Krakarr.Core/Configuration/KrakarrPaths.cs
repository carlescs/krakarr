namespace Krakarr.Core.Configuration;

/// <summary>Well-known filesystem locations, resolved at startup.</summary>
public sealed class KrakarrPaths
{
    public required string ConfigDirectory { get; init; }
    public required string DefaultDownloadDirectory { get; init; }

    public string TorrentCacheDirectory => Path.Combine(ConfigDirectory, "torrent-cache");
    public string TorrentFilesDirectory => Path.Combine(ConfigDirectory, "torrents");
    public string LogsDirectory => Path.Combine(ConfigDirectory, "logs");
    public string BackupsDirectory => Path.Combine(ConfigDirectory, "Backups");
    /// <summary>ASP.NET Core data-protection keys (cookies, protected secrets).</summary>
    public string KeysDirectory => Path.Combine(ConfigDirectory, "keys");
    /// <summary>Optional bootstrap configuration (port, database, URL base); environment variables win over it.</summary>
    public string ConfigFile => Path.Combine(ConfigDirectory, "config.json");
    /// <summary>A validated backup waiting to be restored on the next start.</summary>
    public string PendingRestoreFile => Path.Combine(ConfigDirectory, "restore-pending.zip");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(TorrentCacheDirectory);
        Directory.CreateDirectory(TorrentFilesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
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
