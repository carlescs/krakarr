namespace Krakarr.Core.Configuration;

/// <summary>Marker for settings sections persisted as JSON in the settings table.</summary>
public interface ISettingsSection
{
    static abstract string SectionKey { get; }
}

public sealed record GeneralSettings : ISettingsSection
{
    public static string SectionKey => "general";
    public string ApiKey { get; init; } = string.Empty;
    public string InstanceName { get; init; } = "Krakarr";
}

public sealed record MediaManagementSettings : ISettingsSection
{
    public static string SectionKey => "media";
    public bool RenameFiles { get; init; } = true;
    public string MovieFolderFormat { get; init; } = "{Movie Title} ({Release Year})";
    public string MovieFileFormat { get; init; } = "{Movie Title} ({Release Year}) [{Quality}]";
    public string SeriesFolderFormat { get; init; } = "{Series Title} ({Series Year})";
    public string SeasonFolderFormat { get; init; } = "Season {Season}";
    public string EpisodeFileFormat { get; init; } = "{Series Title} - S{Season}E{Episode} - {Episode Title} [{Quality}]";
    public bool UseHardlinks { get; init; } = true;
    /// <summary>Minimum file size (MB) to consider when picking the main video file of a download.</summary>
    public int MinimumVideoFileSizeMb { get; init; } = 50;
}

public sealed record MetadataSettings : ISettingsSection
{
    public static string SectionKey => "metadata";
    /// <summary>TMDB v3 API key or v4 read access token, used for both movies and series.</summary>
    public string? TmdbApiKey { get; init; }
    public string Language { get; init; } = "en-US";
}

public enum DownloadClientKind
{
    /// <summary>The torrent engine built into Krakarr (MonoTorrent).</summary>
    Embedded = 0,
    /// <summary>An external qBittorrent instance, driven through its WebUI API.</summary>
    QBittorrent = 1,
}

public sealed record QBittorrentSettings
{
    /// <summary>Placeholder returned instead of the stored password; sending it back keeps the stored value.</summary>
    public const string PasswordMask = "********";

    public string Url { get; init; } = "http://localhost:8080";
    public string? Username { get; init; } = "admin";
    public string? Password { get; init; }
    /// <summary>Every torrent Krakarr adds gets this tag; only torrents with it are listed or controlled.</summary>
    public string Tag { get; init; } = "krakarr";
    /// <summary>Optional category override; empty keeps Krakarr's per-grab category (movies, manual, ...).</summary>
    public string? Category { get; init; }
    /// <summary>Optional save path passed to qBittorrent; empty uses qBittorrent's default.</summary>
    public string? SavePath { get; init; }
    /// <summary>Remote path mapping: path prefix as qBittorrent sees it ...</summary>
    public string? RemotePathPrefix { get; init; }
    /// <summary>... and the same location as Krakarr sees it.</summary>
    public string? LocalPathPrefix { get; init; }
}

public sealed record DownloadClientSettings : ISettingsSection
{
    public static string SectionKey => "downloads";
    public DownloadClientKind Client { get; init; } = DownloadClientKind.Embedded;
    public QBittorrentSettings QBittorrent { get; init; } = new();

    // ---- Built-in engine options (ignored when using qBittorrent) ----
    public string? DownloadDirectory { get; init; }
    public int ListenPort { get; init; } = 6881;
    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxDownloadRateKiB { get; init; }
    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxUploadRateKiB { get; init; }
    public int MaxConnections { get; init; } = 200;
    /// <summary>Stop seeding once this ratio is reached (0 = never stop on ratio).</summary>
    public double SeedRatioLimit { get; init; } = 1.0;
    /// <summary>Stop seeding after this many minutes (0 = never stop on time).</summary>
    public int SeedTimeLimitMinutes { get; init; }
    public bool EnableDht { get; init; } = true;
    public bool EnablePortForwarding { get; init; }
}

public sealed record AutomationSettings : ISettingsSection
{
    public static string SectionKey => "automation";
    public int RssSyncIntervalMinutes { get; init; } = 30;
    public bool SearchOnAdd { get; init; } = true;
}

public enum HearingImpairedMode
{
    /// <summary>Use normal subtitles when available, hearing-impaired ones otherwise.</summary>
    PreferNormal = 0,
    /// <summary>Use hearing-impaired subtitles when available.</summary>
    PreferHearingImpaired = 1,
    /// <summary>Never download hearing-impaired subtitles.</summary>
    ExcludeHearingImpaired = 2,
}

public sealed record OpenSubtitlesSettings
{
    public bool Enabled { get; init; } = true;
    /// <summary>Consumer API key from opensubtitles.com.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Optional account; logged-in users get a bigger daily download quota.</summary>
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record SubtitleSettings : ISettingsSection
{
    public const string PasswordMask = "********";
    public static string SectionKey => "subtitles";

    public bool Enabled { get; init; }
    /// <summary>Wanted languages (ISO 639-1, e.g. "en", "es", "pt-br").</summary>
    public List<string> Languages { get; init; } = ["en"];
    public HearingImpairedMode HearingImpaired { get; init; } = HearingImpairedMode.PreferNormal;
    /// <summary>Minimum match score (0-100) for automatic downloads.</summary>
    public int MinimumScoreMovies { get; init; } = 70;
    public int MinimumScoreEpisodes { get; init; } = 80;
    /// <summary>How often files still missing subtitles are searched again.</summary>
    public int SearchIntervalHours { get; init; } = 24;
    public bool IncludeAiTranslated { get; init; }
    public OpenSubtitlesSettings OpenSubtitles { get; init; } = new();
}

/// <summary>Internal bookkeeping: the language set the subtitle search last ran with.</summary>
public sealed record SubtitleSearchState : ISettingsSection
{
    public static string SectionKey => "subtitles-state";
    public string? LanguagesSignature { get; init; }
}
