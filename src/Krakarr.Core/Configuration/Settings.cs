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
    public bool UseHardlinks { get; init; } = true;
    /// <summary>Minimum file size (MB) to consider when picking the main video file of a download.</summary>
    public int MinimumVideoFileSizeMb { get; init; } = 50;
}

public sealed record MetadataSettings : ISettingsSection
{
    public static string SectionKey => "metadata";
    /// <summary>TMDB v3 API key or v4 read access token.</summary>
    public string? TmdbApiKey { get; init; }
    public string? TvdbApiKey { get; init; }
    public string? TvdbPin { get; init; }
    public string Language { get; init; } = "en-US";
}

public sealed record DownloadClientSettings : ISettingsSection
{
    public static string SectionKey => "downloads";
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
