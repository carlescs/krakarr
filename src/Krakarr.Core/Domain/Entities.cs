namespace Krakarr.Core.Domain;

public enum MediaType
{
    Movie = 1,
    Series = 2,
}

public class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }
    public QualityProfile? QualityProfile { get; set; }
    /// <summary>Absolute path of the movie's folder.</summary>
    public required string Path { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSearched { get; set; }
    public int? MovieFileId { get; set; }
    public MovieFile? MovieFile { get; set; }
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
}

public class Series
{
    public int Id { get; set; }
    public int TvdbId { get; set; }
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public required string Title { get; set; }
    public int Year { get; set; }
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public bool Monitored { get; set; } = true;
    public int QualityProfileId { get; set; }
    public required string Path { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public List<Episode> Episodes { get; set; } = [];
}

public class Episode
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int TvdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string? Title { get; set; }
    public DateOnly? AirDate { get; set; }
    public bool Monitored { get; set; } = true;
    public string? RelativePath { get; set; }
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
}
