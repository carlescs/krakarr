namespace Krakarr.Core.Domain;

public enum MediaType
{
    Movie = 1,
    Series = 2,
}

/// <summary>Fixed user roles, from most to least privileged.</summary>
public enum UserRole
{
    /// <summary>Everything, including settings, indexers, download client and user management.</summary>
    Admin = 1,

    /// <summary>Manages the library and downloads (add, search, grab, delete), but not settings or users.</summary>
    Manager = 2,

    /// <summary>Browses Discover and the library read-only.</summary>
    User = 3,
}

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>
    /// Changes whenever the credentials change (password reset); sign-in cookies carrying an older stamp are rejected,
    /// which signs the user out everywhere.
    /// </summary>
    public string SecurityStamp { get; set; } = NewSecurityStamp();

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// A WebAuthn passkey that signs <see cref="User"/> in without a password. Only the public key is stored; the private
/// key never leaves the user's device or password manager.
/// </summary>
public class UserPasskey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Credential id chosen by the authenticator (at most 1023 bytes).</summary>
    public required byte[] CredentialId { get; set; }
    /// <summary>The credential's public key as a COSE_Key (CBOR).</summary>
    public required byte[] PublicKey { get; set; }
    /// <summary>Signature counter last reported by the authenticator; synced passkeys always report 0.</summary>
    public long SignCount { get; set; }
    /// <summary>Transport hints ("internal", "hybrid", "usb"...), comma-separated, passed back to the browser.</summary>
    public string? Transports { get; set; }
    /// <summary>Label the user gave it ("Phone", "YubiKey").</summary>
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Key/value settings store; values are JSON documents per section.</summary>
public class SettingEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

public class RootFolder
{
    public int Id { get; set; }
    public required string Path { get; set; }
    public MediaType MediaType { get; set; } = MediaType.Movie;
}

public class QualityProfile
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>Ids of the qualities (see <see cref="Qualities"/>) that may be grabbed.</summary>
    public List<int> AllowedQualityIds { get; set; } = [];
    /// <summary>Once a file of this quality (or better) exists, stop upgrading.</summary>
    public int CutoffQualityId { get; set; }
    public bool UpgradeAllowed { get; set; } = true;
    /// <summary>Minimum release size in MB (0 = no limit).</summary>
    public long MinSizeMb { get; set; }
    /// <summary>Maximum release size in MB (0 = no limit).</summary>
    public long MaxSizeMb { get; set; }
    /// <summary>Score each <see cref="CustomFormat"/> adds to a release that matches it (formats not listed score 0).</summary>
    public List<ProfileFormatScore> FormatScores { get; set; } = [];
    /// <summary>Releases whose custom format score is below this are rejected.</summary>
    public int MinFormatScore { get; set; }
    /// <summary>"Upgrade until": once a file reaches this custom format score (and the quality cutoff), stop upgrading.</summary>
    public int CutoffFormatScore { get; set; }

    /// <summary>Sum of this profile's scores for the given matched custom formats.</summary>
    public int FormatScore(IEnumerable<int>? customFormatIds)
    {
        if (customFormatIds is null || FormatScores.Count == 0) return 0;
        var total = 0;
        foreach (var id in customFormatIds)
        {
            foreach (var s in FormatScores)
            {
                if (s.FormatId == id) total += s.Score;
            }
        }

        return total;
    }
}

public sealed record ProfileFormatScore(int FormatId, int Score);

/// <summary>
/// A named set of conditions a release can match (Radarr/Sonarr "custom formats"); quality profiles score the formats a release
/// matches to rank releases and decide upgrades.
/// </summary>
public class CustomFormat
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<CustomFormatSpecification> Specifications { get; set; } = [];
}

/// <summary>What a custom format condition looks at.</summary>
public enum CustomFormatCondition
{
    /// <summary>Regular expression on the release title.</summary>
    ReleaseTitle,
    /// <summary>Regular expression on the release group.</summary>
    ReleaseGroup,
    /// <summary>Regular expression on the editions tagged in the title ("Director's Cut", "IMAX"...).</summary>
    Edition,
    /// <summary>Audio language code (MediaLanguages); <see cref="CustomFormatSpecification.ExceptLanguage"/> inverts it.</summary>
    Language,
    /// <summary>Source: Cam, Telesync, Telecine, Workprint, Dvd, Tv, WebDl, WebRip or Bluray (remuxes included).</summary>
    Source,
    /// <summary>Resolution: 480, 576, 720, 1080 or 2160.</summary>
    Resolution,
    /// <summary>Quality modifier: Remux.</summary>
    QualityModifier,
    /// <summary>Size between <see cref="CustomFormatSpecification.Min"/> and <see cref="CustomFormatSpecification.Max"/> GB.</summary>
    Size,
    /// <summary>Regular expression on the indexer name.</summary>
    Indexer,
    /// <summary>HDR10, HDR10+, DV, HLG, "DV-only" (no HDR fallback) or "HDR" (any).</summary>
    Hdr,
    /// <summary>x264, x265, av1 or xvid.</summary>
    VideoCodec,
    /// <summary>Atmos, TrueHD, DTS:X, DTS-HD MA, DD+, AAC... or a channel layout ("7.1", "5.1", "2.0").</summary>
    Audio,
    /// <summary>Streaming service code: AMZN, NF, DSNP, ATVP, HMAX...</summary>
    StreamingService,
    /// <summary>Single episode, multi-episode or season pack.</summary>
    ReleaseType,
    /// <summary>3D, hardcoded subs or upscaled.</summary>
    Flag,
}

public sealed record CustomFormatSpecification
{
    public string Name { get; init; } = string.Empty;
    public CustomFormatCondition Condition { get; init; }
    public string Value { get; init; } = string.Empty;
    /// <summary>Size conditions: minimum size in GB (0 = none).</summary>
    public double Min { get; init; }
    /// <summary>Size conditions: maximum size in GB (0 = none).</summary>
    public double Max { get; init; }
    /// <summary>Language conditions: match releases with any language other than <see cref="Value"/>.</summary>
    public bool ExceptLanguage { get; init; }
    /// <summary>The condition matches when its test fails.</summary>
    public bool Negate { get; init; }
    /// <summary>The format only matches when this condition does (otherwise one condition of each kind is enough).</summary>
    public bool Required { get; init; }
}

public enum IndexerProtocol
{
    Torznab = 1,
    Newznab = 2,
}

public class Indexer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public IndexerProtocol Protocol { get; set; } = IndexerProtocol.Torznab;
    public required string BaseUrl { get; set; }
    /// <summary>Path appended to the base url, typically "/api".</summary>
    public string ApiPath { get; set; } = "/api";
    public string? ApiKey { get; set; }
    public List<int> MovieCategories { get; set; } = [2000];
    public List<int> TvCategories { get; set; } = [5000];
    public bool Enabled { get; set; } = true;
    public bool EnableRss { get; set; } = true;
    public int Priority { get; set; } = 25;
    public int MinimumSeeders { get; set; } = 1;
    /// <summary>
    /// Audio language assumed for releases from this indexer whose names carry no language tag
    /// (<see cref="MediaLanguages"/> code). Null = English, the scene convention.
    /// </summary>
    public string? DefaultLanguage { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
}

public enum MovieStatus
{
    Announced = 0,
    InCinemas = 1,
    Released = 2,
}

/// <summary>Scores and facts read from OMDb, stored on movies and series (see <c>OmdbInfo</c>).</summary>
public interface IOmdbFields
{
    string? ImdbId { get; }
    double? ImdbRating { get; set; }
    long? ImdbVotes { get; set; }
    int? RottenTomatoes { get; set; }
    int? Metacritic { get; set; }
    string? RottenTomatoesUrl { get; set; }
    /// <summary>Content rating ("PG-13", "TV-MA").</summary>
    string? Certification { get; set; }
    string? Awards { get; set; }
    string? BoxOffice { get; set; }
    string? Director { get; set; }
    string? Writer { get; set; }
    /// <summary>OMDb's English plot, shown only when the metadata source has no overview.</summary>
    string? OmdbPlot { get; set; }
    /// <summary>When OMDb was last asked; null = never (or the IMDb id changed since).</summary>
    DateTimeOffset? OmdbUpdated { get; set; }
}

public class Movie : IOmdbFields
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public required string Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string SortTitle { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? FanartUrl { get; set; }
    public int Runtime { get; set; }
    public MovieStatus Status { get; set; } = MovieStatus.Released;
    /// <summary>Release dates per country (theatrical, digital, physical), from TMDB.</summary>
    public List<MovieReleaseDate> ReleaseDates { get; set; } = [];
    /// <summary>Earliest cinema release in any country.</summary>
    public DateOnly? InCinemas { get; set; }
    /// <summary>Earliest digital release in any country.</summary>
    public DateOnly? DigitalRelease { get; set; }
    /// <summary>Earliest physical release in any country.</summary>
    public DateOnly? PhysicalRelease { get; set; }
    /// <summary>Automatic searches wait until the movie reaches this status. Movies added before this existed use Announced.</summary>
    public MinimumAvailability MinimumAvailability { get; set; } = MinimumAvailability.Released;
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }
    /// <summary>Wanted audio languages for this item, most preferred first; null = the global default (<c>LanguageSettings</c>).</summary>
    public List<string>? Languages { get; set; }
    public QualityProfile? QualityProfile { get; set; }
    /// <summary>Absolute path of the movie's folder.</summary>
    public required string Path { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSearched { get; set; }
    public int? MovieFileId { get; set; }
    public MovieFile? MovieFile { get; set; }
    /// <summary>TMDB collection (franchise) the movie belongs to, if any.</summary>
    public int? CollectionId { get; set; }
    public MovieCollection? Collection { get; set; }
    /// <summary>Other names releases use for the movie. Load it only for the movie being matched or searched.</summary>
    public List<AlternateTitle> AlternateTitles { get; set; } = [];
    // ---- OMDb (IOmdbFields): refreshed by the OMDb task, keyed on ImdbId ----
    public double? ImdbRating { get; set; }
    public long? ImdbVotes { get; set; }
    public int? RottenTomatoes { get; set; }
    public int? Metacritic { get; set; }
    public string? RottenTomatoesUrl { get; set; }
    public string? Certification { get; set; }
    public string? Awards { get; set; }
    public string? BoxOffice { get; set; }
    public string? Director { get; set; }
    public string? Writer { get; set; }
    public string? OmdbPlot { get; set; }
    public DateTimeOffset? OmdbUpdated { get; set; }
}

/// <summary>A TMDB movie collection ("The Lord of the Rings Collection"). Only collections with movies in the library are kept.</summary>
public class MovieCollection
{
    public int Id { get; set; }
    public int TmdbId { get; set; }
    public required string Name { get; set; }
    public string SortName { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? FanartUrl { get; set; }
}

/// <summary>What movie and episode files share: the parts of a library file that are read from the file itself.</summary>
public interface ILibraryFile
{
    int QualityId { get; set; }
    string? SceneName { get; set; }
    /// <summary>Ids of the custom formats the file's release matched when it was imported (null = not evaluated).</summary>
    List<int>? CustomFormatIds { get; set; }
    /// <summary>Custom format score of the file's release under its profile when it was imported.</summary>
    int CustomFormatScore { get; set; }
    string? EmbeddedSubtitles { get; set; }
    string? MediaInfo { get; set; }
}

public class MovieFile : ILibraryFile
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    /// <summary>Path relative to <see cref="Movie.Path"/>.</summary>
    public required string RelativePath { get; set; }
    public long Size { get; set; }
    public int QualityId { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? SceneName { get; set; }
    public List<int>? CustomFormatIds { get; set; }
    public int CustomFormatScore { get; set; }
    public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When subtitles were last searched for this file (null = never).</summary>
    public DateTimeOffset? LastSubtitleSearch { get; set; }
    /// <summary>
    /// Subtitle tracks inside the video, as comma-separated "lang[+hi][+forced]" tokens ("" = none, null = not read yet).
    /// </summary>
    public string? EmbeddedSubtitles { get; set; }
    /// <summary>
    /// Video, audio and duration read from the file's headers, as compact JSON (<c>MediaInfoSummary</c> in Krakarr.Media;
    /// "" = nothing could be read, null = not read yet).
    /// </summary>
    public string? MediaInfo { get; set; }
    /// <summary>
    /// When a search of every configured provider first found nothing for a missing wanted language, or the user marked
    /// it by hand (null = not marked). The scheduled subtitle search skips marked files; a new or replaced file, or a change of wanted
    /// languages or providers clears the mark, and manual searches still run.
    /// </summary>
    public DateTimeOffset? SubtitlesNotFound { get; set; }
}

public enum SeriesStatus
{
    Continuing = 0,
    Ended = 1,
    Upcoming = 2,
}

/// <summary>How episodes are monitored when a series is added.</summary>
public enum MonitorMode
{
    All = 0,
    Future = 1,
    Missing = 2,
    FirstSeason = 3,
    LatestSeason = 4,
    None = 5,
}

public class Series : IOmdbFields
{
    public int Id { get; set; }
    /// <summary>TMDB TV id. Always set when series metadata comes from TMDB; otherwise when TheTVDB knows it.</summary>
    public int? TmdbId { get; set; }
    /// <summary>TheTVDB id. Always set when series metadata comes from TheTVDB; otherwise when TMDB knows it. Indexers search and tag TV releases by it.</summary>
    public int? TvdbId { get; set; }
    public string? ImdbId { get; set; }
    /// <summary>Display title, used for naming and matching. Normally the metadata title, but the user can edit it (e.g. to tell apart two series with the same name).</summary>
    public required string Title { get; set; }
    /// <summary>Title as the metadata source names the series. When <see cref="Title"/> differs, the user edited it and refreshes keep the edit.</summary>
    public string MetadataTitle { get; set; } = string.Empty;
    public string SortTitle { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? FanartUrl { get; set; }
    public string? Network { get; set; }
    public int Runtime { get; set; }
    public SeriesStatus Status { get; set; } = SeriesStatus.Continuing;
    public DateOnly? FirstAired { get; set; }
    public bool Monitored { get; set; } = true;
    /// <summary>Put episodes in "Season NN" sub-folders.</summary>
    public bool SeasonFolder { get; set; } = true;
    public int QualityProfileId { get; set; }
    /// <summary>Wanted audio languages for this item, most preferred first; null = the global default (<c>LanguageSettings</c>).</summary>
    public List<string>? Languages { get; set; }
    public QualityProfile? QualityProfile { get; set; }
    /// <summary>Absolute path of the series folder.</summary>
    public required string Path { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRefreshed { get; set; }
    public DateTimeOffset? LastSearched { get; set; }
    public List<Season> Seasons { get; set; } = [];
    public List<Episode> Episodes { get; set; } = [];
    /// <summary>Other names releases use for the series. Load it only for the series being matched or searched.</summary>
    public List<AlternateTitle> AlternateTitles { get; set; } = [];
    // ---- OMDb (IOmdbFields): refreshed by the OMDb task, keyed on ImdbId ----
    public double? ImdbRating { get; set; }
    public long? ImdbVotes { get; set; }
    public int? RottenTomatoes { get; set; }
    public int? Metacritic { get; set; }
    public string? RottenTomatoesUrl { get; set; }
    public string? Certification { get; set; }
    public string? Awards { get; set; }
    public string? BoxOffice { get; set; }
    public string? Director { get; set; }
    public string? Writer { get; set; }
    public string? OmdbPlot { get; set; }
    public DateTimeOffset? OmdbUpdated { get; set; }
}

public enum AlternateTitleSource
{
    /// <summary>TMDB alternative titles and translations, TheTVDB aliases. Replaced on every refresh.</summary>
    Metadata = 0,
    /// <summary>Added by the user; refreshes never touch them.</summary>
    User = 1,
    /// <summary>Scene names from TheXEM. Replaced on every refresh.</summary>
    Scene = 2,
}

/// <summary>Another name of a movie or series (local title, original title, AKA, scene name) that releases may use.</summary>
public class AlternateTitle
{
    public const int MaxTitleLength = 200;

    public int Id { get; set; }
    /// <summary>Set for a movie's title; <see cref="SeriesId"/> is null then.</summary>
    public int? MovieId { get; set; }
    /// <summary>Set for a series' title; <see cref="MovieId"/> is null then.</summary>
    public int? SeriesId { get; set; }
    public required string Title { get; set; }
    /// <summary>
    /// <see cref="Title"/> normalised the way release names are (<c>ReleaseParser.NormalizeTitle</c>; for series without a
    /// trailing year), so RSS sync can look titles up without loading the library.
    /// </summary>
    public required string NormalizedTitle { get; set; }
    /// <summary>
    /// Language the title is in (<see cref="MediaLanguages"/> code), when known. Text searches use titles in the indexer's
    /// default language; a null language is searched on every indexer.
    /// </summary>
    public string? Language { get; set; }
    public AlternateTitleSource Source { get; set; }
}

public class Season
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public bool Monitored { get; set; } = true;
}

public class Episode
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    /// <summary>Episode ids from the metadata sources that have provided this episode.</summary>
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public int? AbsoluteNumber { get; set; }
    /// <summary>Season number scene releases use for this episode (TheXEM), when it differs or is mapped; null = not mapped.</summary>
    public int? SceneSeasonNumber { get; set; }
    /// <summary>Episode number scene releases use for this episode (TheXEM); set together with <see cref="SceneSeasonNumber"/>.</summary>
    public int? SceneEpisodeNumber { get; set; }
    public string? Title { get; set; }
    public string? Overview { get; set; }
    public DateOnly? AirDate { get; set; }
    public int Runtime { get; set; }
    public bool Monitored { get; set; } = true;
    public int? EpisodeFileId { get; set; }
    public EpisodeFile? EpisodeFile { get; set; }

    public bool HasAired(DateOnly today) => AirDate is { } d && d <= today;
}

/// <summary>A video file in the library holding one or more episodes of the same season.</summary>
public class EpisodeFile : ILibraryFile
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    /// <summary>Path relative to <see cref="Series.Path"/>.</summary>
    public required string RelativePath { get; set; }
    public long Size { get; set; }
    public int QualityId { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? SceneName { get; set; }
    public List<int>? CustomFormatIds { get; set; }
    public int CustomFormatScore { get; set; }
    public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When subtitles were last searched for this file (null = never).</summary>
    public DateTimeOffset? LastSubtitleSearch { get; set; }
    /// <summary>
    /// Subtitle tracks inside the video, as comma-separated "lang[+hi][+forced]" tokens ("" = none, null = not read yet).
    /// </summary>
    public string? EmbeddedSubtitles { get; set; }
    /// <summary>
    /// Video, audio and duration read from the file's headers, as compact JSON (<c>MediaInfoSummary</c> in Krakarr.Media;
    /// "" = nothing could be read, null = not read yet).
    /// </summary>
    public string? MediaInfo { get; set; }
    /// <summary>
    /// When the user marked this file "not found" (null = not marked). Unlike movie files, episode files are only marked
    /// by hand: the scheduled subtitle search skips them until retried; a replaced file or a change of wanted languages
    /// or providers clears the mark, and manual searches still run.
    /// </summary>
    public DateTimeOffset? SubtitlesNotFound { get; set; }
}

/// <summary>An external subtitle file stored next to a movie or episode file.</summary>
public class SubtitleFile
{
    public int Id { get; set; }
    public int? MovieFileId { get; set; }
    public int? EpisodeFileId { get; set; }
    /// <summary>ISO 639-1 code, or a regional variant such as "pt-br".</summary>
    public required string Language { get; set; }
    public bool HearingImpaired { get; set; }
    public bool Forced { get; set; }
    /// <summary>Absolute path of the subtitle file.</summary>
    public required string Path { get; set; }
    /// <summary>Provider it came from, or "disk" for files found next to the video.</summary>
    public required string Provider { get; set; }
    public int? Score { get; set; }
    public string? Release { get; set; }
    public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Picked by hand in the interactive search; automatic upgrades leave it alone.</summary>
    public bool Manual { get; set; }
    /// <summary>When the subtitle was last resynchronised against the video (automatically or by hand).</summary>
    public DateTimeOffset? SyncedAt { get; set; }
    /// <summary>How the last sync went (reference and tool, or why it failed); automatic sync skips rows that have one.</summary>
    public string? SyncNote { get; set; }
}

public enum DownloadState
{
    Queued = 0,
    FetchingMetadata = 1,
    Downloading = 2,
    Paused = 3,
    Seeding = 4,
    Completed = 5,
    Error = 6,
}

public static class DownloadClients
{
    public const string Embedded = "embedded";
    public const string QBittorrent = "qbittorrent";
}

/// <summary>A torrent tracked by Krakarr, in the built-in engine or an external client.</summary>
public class Download
{
    public int Id { get; set; }
    /// <summary>Which client owns the torrent (<see cref="DownloadClients"/>).</summary>
    public string Client { get; set; } = DownloadClients.Embedded;
    /// <summary>Lower-case hex info hash (v1 or v2).</summary>
    public required string InfoHash { get; set; }
    public required string Name { get; set; }
    public string Category { get; set; } = "default";
    public string? MagnetUri { get; set; }
    /// <summary>Path of the stored .torrent file, if added from a torrent file.</summary>
    public string? TorrentFilePath { get; set; }
    public required string SavePath { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public bool Paused { get; set; }
    public long Size { get; set; }
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    // Media linkage (null for manual downloads).
    public MediaType? MediaType { get; set; }
    public int? MovieId { get; set; }
    public int? SeriesId { get; set; }
    /// <summary>Episodes this download is expected to contain (series downloads).</summary>
    public List<int> EpisodeIds { get; set; } = [];
    public int? QualityId { get; set; }
    public string? IndexerName { get; set; }
    public bool Imported { get; set; }
    public DateTimeOffset? ImportedAt { get; set; }
    /// <summary>Set when automatic import failed; import is not retried until cleared.</summary>
    public string? ImportMessage { get; set; }
}

public enum HistoryEventType
{
    Grabbed = 1,
    DownloadFailed = 2,
    Imported = 3,
    FileDeleted = 4,
    SubtitleDownloaded = 5,
}

public class HistoryEntry
{
    public int Id { get; set; }
    public HistoryEventType EventType { get; set; }
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    public required string SourceTitle { get; set; }
    public int? MovieId { get; set; }
    public int? SeriesId { get; set; }
    public int? QualityId { get; set; }
    public string? Indexer { get; set; }
    public string? Message { get; set; }
    /// <summary>Subtitle language for <see cref="HistoryEventType.SubtitleDownloaded"/> entries.</summary>
    public string? Language { get; set; }
}

public enum RequestStatus
{
    Pending = 0,
    Approved = 1,
    Declined = 2,
}

/// <summary>
/// A user's request to add a movie or series to the library (Seerr-style). Plain users create them; admins and
/// managers approve (which adds the title) or decline them. Title, year and poster are a snapshot for the list.
/// </summary>
public class MediaRequest
{
    public int Id { get; set; }
    public MediaType MediaType { get; set; }
    /// <summary>TMDB id of the movie or show; requests come from TMDB discovery and detail pages.</summary>
    public int TmdbId { get; set; }
    /// <summary>TheTVDB id of a show, when TMDB knows it.</summary>
    public int? TvdbId { get; set; }
    public required string Title { get; set; }
    public int Year { get; set; }
    public string? PosterUrl { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public int RequestedById { get; set; }
    public User? RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Who approved or declined it; null while pending, or when it was approved by adding the title directly.</summary>
    public int? ProcessedById { get; set; }
    public User? ProcessedBy { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    /// <summary>Optional reason given when declining.</summary>
    public string? DeclineReason { get; set; }
    /// <summary>
    /// The request was approved or declined and the requester hasn't seen the result yet: it shows in their
    /// notification bell until they open the Requests page.
    /// </summary>
    public bool ResultUnseen { get; set; }
}

/// <summary>
/// A user following a library movie or series: it shows in their "My titles" calendar and list filters. Users subscribe
/// from the title's page; requesters are subscribed automatically once their request is in the library.
/// Exactly one of <see cref="MovieId"/> and <see cref="SeriesId"/> is set.
/// </summary>
public class MediaSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int? MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int? SeriesId { get; set; }
    public Series? Series { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A notification the user dismissed from the bell. Hides that <see cref="NotificationId"/> until its source reports
/// newer activity (a later <c>Date</c>, or a higher <c>Count</c> at the same date).
/// </summary>
public class NotificationDismissal
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public required string NotificationId { get; set; }
    public int Count { get; set; }
    public DateTimeOffset? DismissedAt { get; set; }
}


/// <summary>
/// A browser or installed app that receives push notifications for <see cref="User"/> (Web Push). The endpoint and
/// keys come from the browser's <c>PushManager.subscribe()</c>; the push service deletes the endpoint (404/410) when
/// the user revokes the permission or uninstalls the app, and the subscription is then removed.
/// </summary>
public class PushSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    /// <summary>Push service URL to POST encrypted messages to; unique per device.</summary>
    public required string Endpoint { get; set; }
    /// <summary>The device's P-256 public key (base64url, uncompressed point).</summary>
    public required string P256dh { get; set; }
    /// <summary>The device's 16-byte authentication secret (base64url).</summary>
    public required string Auth { get; set; }
    /// <summary>What the device wants to be told about.</summary>
    public Push.PushTopics Topics { get; set; } = Push.PushTopics.All;
    /// <summary>Label shown in the device list, e.g. "Chrome on Android".</summary>
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Column sizes of <see cref="PushSubscription"/>, checked by the endpoints before saving.</summary>
public static class PushSubscriptionLimits
{
    public const int Endpoint = 2048;
    public const int Key = 200;
    public const int Name = 100;
}
