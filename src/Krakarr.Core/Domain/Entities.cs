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

public class Movie
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

public class MovieFile
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    /// <summary>Path relative to <see cref="Movie.Path"/>.</summary>
    public required string RelativePath { get; set; }
    public long Size { get; set; }
    public int QualityId { get; set; }
    public string? ReleaseGroup { get; set; }
    public string? SceneName { get; set; }
    public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When subtitles were last searched for this file (null = never).</summary>
    public DateTimeOffset? LastSubtitleSearch { get; set; }
    /// <summary>
    /// Subtitle tracks inside the video, as comma-separated "lang[+hi][+forced]" tokens ("" = none, null = not read yet).
    /// </summary>
    public string? EmbeddedSubtitles { get; set; }
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

public class Series
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
public class EpisodeFile
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
    public DateTimeOffset DateAdded { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When subtitles were last searched for this file (null = never).</summary>
    public DateTimeOffset? LastSubtitleSearch { get; set; }
    /// <summary>
    /// Subtitle tracks inside the video, as comma-separated "lang[+hi][+forced]" tokens ("" = none, null = not read yet).
    /// </summary>
    public string? EmbeddedSubtitles { get; set; }
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
