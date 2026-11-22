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

/// <summary>Where series, season and episode metadata comes from. Movies always use TMDB.</summary>
public enum SeriesMetadataSource
{
    Tmdb = 0,
    Tvdb = 1,
}

public sealed record MetadataSettings : ISettingsSection
{
    public static string SectionKey => "metadata";
    /// <summary>TMDB v3 API key or v4 read access token. Used for movies, for series when <see cref="SeriesSource"/> is TMDB, and for Discover recommendations.</summary>
    public string? TmdbApiKey { get; init; }
    public SeriesMetadataSource SeriesSource { get; init; } = SeriesMetadataSource.Tmdb;
    public string? TvdbApiKey { get; init; }
    public string? TvdbPin { get; init; }
    /// <summary>OMDb API key (omdbapi.com). Optional: enables IMDb, Rotten Tomatoes and Metacritic scores on detail pages.</summary>
    public string? OmdbApiKey { get; init; }
    public string Language { get; init; } = "en-US";
    /// <summary>Country (ISO 3166-1 alpha-2) whose movie release dates the calendar prefers; empty = earliest worldwide.</summary>
    public string? ReleaseRegion { get; init; }
}

/// <summary>Audio languages releases must have to be grabbed. Movies and series use these unless they set their own.</summary>
public sealed record LanguageSettings : ISettingsSection
{
    public static string SectionKey => "languages";

    /// <summary>
    /// Wanted audio languages (<see cref="Domain.MediaLanguages"/> codes), most preferred first. Releases in none of them are
    /// rejected, and among acceptable releases an earlier language wins over a better quality. Empty = any language.
    /// </summary>
    public List<string> Preferred { get; init; } = [];

    /// <summary>The languages that apply to an item: its own list when it has one, otherwise <see cref="Preferred"/>.</summary>
    public IReadOnlyList<string> For(IReadOnlyList<string>? itemLanguages) => itemLanguages ?? Preferred;
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
    /// <summary>Placeholder returned instead of the stored password or API key; sending it back keeps the stored value.</summary>
    public const string PasswordMask = "********";

    public string Url { get; init; } = "http://localhost:8080";
    public string? Username { get; init; } = "admin";
    public string? Password { get; init; }
    /// <summary>WebUI API key (qBittorrent 5.2+). When set it is sent as a bearer token and the username/password login is skipped.</summary>
    public string? ApiKey { get; init; }
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

    /// <summary>True when an API key is configured, so requests use it instead of the cookie login.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Replaces masked secrets (<see cref="PasswordMask"/>) with the stored values.</summary>
    public QBittorrentSettings WithSecretsFrom(QBittorrentSettings stored) => this with
    {
        Password = Password == PasswordMask ? stored.Password : Password,
        ApiKey = ApiKey == PasswordMask ? stored.ApiKey : ApiKey,
    };

    /// <summary>Copy safe to return to the UI: secrets that are set are replaced by <see cref="PasswordMask"/>.</summary>
    public QBittorrentSettings Masked() => this with
    {
        Password = string.IsNullOrEmpty(Password) ? Password : PasswordMask,
        ApiKey = string.IsNullOrEmpty(ApiKey) ? ApiKey : PasswordMask,
    };
}

public enum SeedLimitAction
{
    /// <summary>Remove the torrent from the client. Imported media downloads also have their data deleted (the library keeps its copy).</summary>
    Remove = 0,
    /// <summary>Stop (pause) the torrent and keep it in the client.</summary>
    Stop = 1,
}

public sealed record DownloadClientSettings : ISettingsSection
{
    public static string SectionKey => "downloads";
    public DownloadClientKind Client { get; init; } = DownloadClientKind.Embedded;
    public QBittorrentSettings QBittorrent { get; init; } = new();

    // ---- Seeding policy (applies to every client) ----
    /// <summary>Seeding is done once this ratio is reached (0 = never on ratio).</summary>
    public double SeedRatioLimit { get; init; } = 1.0;
    /// <summary>Seeding is done after this many minutes since completion (0 = never on time).</summary>
    public int SeedTimeLimitMinutes { get; init; }
    /// <summary>What to do with a torrent once its seeding limit is reached (and its media, if any, is imported).</summary>
    public SeedLimitAction SeedLimitAction { get; init; } = SeedLimitAction.Remove;

    // ---- Built-in engine options (ignored when using qBittorrent) ----
    public string? DownloadDirectory { get; init; }
    public int ListenPort { get; init; } = 6881;
    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxDownloadRateKiB { get; init; }
    /// <summary>KiB/s, 0 = unlimited.</summary>
    public int MaxUploadRateKiB { get; init; }
    public int MaxConnections { get; init; } = 200;
    public bool EnableDht { get; init; } = true;
    public bool EnablePortForwarding { get; init; }
}

public sealed record AutomationSettings : ISettingsSection
{
    public static string SectionKey => "automation";
    public int RssSyncIntervalMinutes { get; init; } = 30;
    public bool SearchOnAdd { get; init; } = true;

    /// <summary>Time between RSS syncs, or <c>null</c> when automatic RSS sync is off.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? RssSyncInterval => RssSyncIntervalMinutes > 0 ? TimeSpan.FromMinutes(RssSyncIntervalMinutes) : null;
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
    /// <summary>Count subtitle tracks inside the video file (MKV, MP4) as present, so they are not searched again.</summary>
    public bool UseEmbeddedSubtitles { get; init; } = true;
    public OpenSubtitlesSettings OpenSubtitles { get; init; } = new();
}

/// <summary>Internal bookkeeping: the language set the subtitle search last ran with.</summary>
public sealed record SubtitleSearchState : ISettingsSection
{
    public static string SectionKey => "subtitles-state";
    public string? LanguagesSignature { get; init; }
}

/// <summary>Optional Jellyfin server; when set, downloaded movies and series link to their Jellyfin page.</summary>
public sealed record JellyfinSettings : ISettingsSection
{
    /// <summary>Placeholder returned instead of the stored API key; sending it back keeps the stored value.</summary>
    public const string ApiKeyMask = "********";
    public static string SectionKey => "jellyfin";

    /// <summary>Address Krakarr uses to reach Jellyfin (e.g. http://jellyfin:8096). Empty = integration off.</summary>
    public string? Url { get; init; }
    /// <summary>API key from Jellyfin → Dashboard → API Keys.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Address the browser opens, when it differs from <see cref="Url"/> (e.g. https://jellyfin.example.com).</summary>
    public string? PublicUrl { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>Transport of the OTLP exporter.</summary>
public enum OtlpProtocol
{
    /// <summary>OTLP over gRPC, usually port 4317.</summary>
    Grpc = 0,
    /// <summary>OTLP over HTTP with protobuf payloads, usually port 4318.</summary>
    HttpProtobuf = 1,
}

/// <summary>
/// Optional OpenTelemetry export (traces, metrics, logs) to an OTLP endpoint such as the OpenTelemetry Collector,
/// Grafana Alloy, Jaeger, Tempo, SigNoz or Aspire Dashboard. Changes apply without a restart.
/// </summary>
public sealed record TelemetrySettings : ISettingsSection
{
    /// <summary>Placeholder returned instead of the stored headers; sending it back keeps the stored value.</summary>
    public const string HeadersMask = "********";
    public static string SectionKey => "telemetry";

    public bool Enabled { get; init; }
    /// <summary>
    /// OTLP endpoint, e.g. http://otel-collector:4317 (gRPC) or http://otel-collector:4318 (HTTP). For HTTP the
    /// per-signal paths (/v1/traces, /v1/metrics, /v1/logs) are added. Empty = OTEL_EXPORTER_OTLP_ENDPOINT or the default.
    /// </summary>
    public string? Endpoint { get; init; } = "http://localhost:4317";
    public OtlpProtocol Protocol { get; init; } = OtlpProtocol.Grpc;
    /// <summary>Extra request headers, e.g. for authentication: <c>Authorization=Bearer abc,X-Scope-OrgID=home</c>.</summary>
    public string? Headers { get; init; }
    /// <summary>The <c>service.name</c> resource attribute.</summary>
    public string ServiceName { get; init; } = "krakarr";
    /// <summary>Extra resource attributes, e.g. <c>deployment.environment=home,host.name=nas</c>.</summary>
    public string? ResourceAttributes { get; init; }

    public bool ExportTraces { get; init; } = true;
    public bool ExportMetrics { get; init; } = true;
    public bool ExportLogs { get; init; }

    /// <summary>Fraction of traces kept (0-1). Child spans follow their parent's decision.</summary>
    public double TraceSampleRatio { get; init; } = 1.0;
    /// <summary>How often metrics are exported.</summary>
    public int MetricExportIntervalSeconds { get; init; } = 60;
    /// <summary>How often library totals (movies, series, episodes, sizes) are recounted for the library gauges.</summary>
    public int LibraryStatsIntervalMinutes { get; init; } = 5;

    /// <summary>Parses <c>key=value,key2=value2</c> (the OTEL_EXPORTER_OTLP_HEADERS / OTEL_RESOURCE_ATTRIBUTES format).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParsePairs(string? value)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) throw new FormatException($"'{part}' is not a key=value pair");
            result.Add(new(Uri.UnescapeDataString(part[..eq].Trim()), Uri.UnescapeDataString(part[(eq + 1)..].Trim())));
        }

        return result;
    }

    /// <summary>A problem with the settings, or null when they are usable.</summary>
    public string? Validate()
    {
        if (!string.IsNullOrWhiteSpace(Endpoint)
            && (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)))
            return "Endpoint must be an absolute http(s) URL, e.g. http://otel-collector:4317";
        if (string.IsNullOrWhiteSpace(ServiceName)) return "Service name is required";
        if (TraceSampleRatio is < 0 or > 1 || double.IsNaN(TraceSampleRatio)) return "Trace sample ratio must be between 0 and 1";
        if (MetricExportIntervalSeconds is < 5 or > 3600) return "Metric export interval must be between 5 and 3600 seconds";
        if (LibraryStatsIntervalMinutes is < 1 or > 1440) return "Library stats interval must be between 1 and 1440 minutes";
        try
        {
            if (Headers != HeadersMask) ParsePairs(Headers);
        }
        catch (FormatException ex)
        {
            return $"Headers: {ex.Message}";
        }

        try
        {
            ParsePairs(ResourceAttributes);
        }
        catch (FormatException ex)
        {
            return $"Resource attributes: {ex.Message}";
        }

        return null;
    }
}
