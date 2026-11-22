export type MediaType = 'Movie' | 'Series';
export type DownloadState = 'Queued' | 'FetchingMetadata' | 'Downloading' | 'Paused' | 'Seeding' | 'Completed' | 'Error';
export type MovieStatus = 'Announced' | 'InCinemas' | 'Released';
export type MinimumAvailability = 'Announced' | 'InCinemas' | 'Released';
export type ReleaseDateType = 'Theatrical' | 'Digital' | 'Physical';
export type HistoryEventType = 'Grabbed' | 'DownloadFailed' | 'Imported' | 'FileDeleted' | 'SubtitleDownloaded';
export type CommandStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';
export type SearchType = 'Generic' | 'Movie' | 'Tv';

export type UserRole = 'Admin' | 'Manager' | 'User';
export interface AuthStatus { setupRequired: boolean; authenticated: boolean; username?: string; role?: UserRole | null; }
export interface UserAccount { id: number; username: string; role: UserRole; createdAt: string; }

export interface Quality { id: number; name: string; source: string; resolution: number; weight: number; }

export interface QualityProfile {
  id: number; name: string; allowedQualityIds: number[]; cutoffQualityId: number;
  upgradeAllowed: boolean; minSizeMb: number; maxSizeMb: number;
}

export interface RootFolder { id: number; path: string; mediaType: MediaType; accessible: boolean; freeSpace?: number; }

export interface Indexer {
  id: number; name: string; protocol: 'Torznab' | 'Newznab'; baseUrl: string; apiPath: string; apiKey?: string | null;
  movieCategories: number[]; tvCategories: number[]; enabled: boolean; enableRss: boolean; priority: number; minimumSeeders: number;
  /** Audio language assumed for untagged releases from this indexer; null = English. */
  defaultLanguage?: string | null;
}

export interface IndexerCapabilities {
  searchAvailable: boolean; movieSearchAvailable: boolean; movieSearchParams: string[];
  tvSearchAvailable: boolean; tvSearchParams: string[]; categories: IndexerCategory[];
}

export interface IndexerCategory { id: number; name: string; parentId?: number | null; }
export interface IndexerTestResult { success: boolean; message?: string; capabilities?: IndexerCapabilities; }
/** An indexer configured in Prowlarr; `existingId` is the Krakarr indexer it was imported as before. */
export interface ProwlarrIndexer { id: number; name: string; protocol: string; supported: boolean; enabled: boolean; priority: number; existingId?: number | null; }
export interface ProwlarrListResult { success: boolean; message?: string | null; indexers: ProwlarrIndexer[]; }
export interface ProwlarrImportResult { added: number; updated: number; }
export interface IndexerError { indexerId: number; indexerName: string; message: string; }

export type IndexerQueryState = 'Pending' | 'Done' | 'Failed';
/** How one indexer's query in a search stands: `releases` once done, `message` when it failed, `seconds` once finished. */
export interface IndexerQueryStatus {
  indexerId: number; indexerName: string; state: IndexerQueryState; releases?: number | null; message?: string | null; seconds?: number | null;
}
/**
 * One line of a streamed interactive search: first `indexers` (all pending), then `releases` each time an indexer
 * answers (its new rows, replacing the rows whose release guids are in `replaced`), and last `done` with the final
 * statuses and every row's release guid in rank order.
 */
export interface SearchStreamEvent<T> {
  type: 'indexers' | 'releases' | 'done';
  indexers?: IndexerQueryStatus[] | null; indexer?: IndexerQueryStatus | null; releases?: T[] | null; replaced?: string[] | null; order?: string[] | null;
}

export interface ReleaseInfo {
  title: string; guid: string; downloadUrl?: string; magnetUrl?: string; infoHash?: string; infoUrl?: string;
  size: number; seeders?: number; peers?: number; publishDate?: string; imdbId?: number; tmdbId?: number; tvdbId?: number;
  categories: number[]; indexerId: number; indexerName: string; indexerPriority: number;
  languages?: string[]; indexerLanguage?: string | null;
}

export interface SearchResult { release: ReleaseInfo; quality: string; parsedTitle?: string; parsedYear?: number; }
export interface ReleaseDecision { release: ReleaseInfo; quality: string; qualityWeight: number; approved: boolean; rejections: string[]; languages: string[]; }

export interface MovieFile { id: number; relativePath: string; size: number; quality: string; releaseGroup?: string; dateAdded: string; }

export interface ReleaseDateRef { date: string; country: string; }
/** A release type's date in the configured region and the earliest worldwide. */
export interface MovieRelease { type: ReleaseDateType; region?: ReleaseDateRef | null; earliest?: ReleaseDateRef | null; }

export interface Movie {
  id: number; tmdbId: number; imdbId?: string; title: string; originalTitle?: string; year: number; overview?: string;
  posterUrl?: string; fanartUrl?: string; runtime: number; status: MovieStatus; monitored: boolean; qualityProfileId: number;
  path: string; added: string; hasFile: boolean; movieFile?: MovieFile; downloadProgress?: number;
  inCinemas?: string | null; digitalRelease?: string | null; physicalRelease?: string | null;
  releases: MovieRelease[]; releaseRegion?: string | null; minimumAvailability: MinimumAvailability; isAvailable: boolean;
  collectionId?: number | null;
  /** Wanted audio languages for this movie; null = the global default. */
  languages?: string[] | null;
}

/** Row of the movie grid (GET /movies). */
export interface MovieListItem {
  id: number; title: string; year: number; posterUrl?: string | null; monitored: boolean; hasFile: boolean;
  quality?: string | null; downloadProgress?: number | null; collectionId?: number | null;
}
/** Outcome of a bulk action: how many titles it applied to, and why it couldn't for the rest. */
export interface BulkResult { affected: number; errors: string[]; }
/** A TMDB collection ("The Lord of the Rings Collection") with at least one movie in the library. */
export interface MovieCollection { id: number; tmdbId: number; name: string; posterUrl?: string | null; }

export type CalendarEntryKind = 'Movie' | 'Episode';
export interface CalendarEntry {
  kind: CalendarEntryKind; date: string;
  releaseType?: ReleaseDateType | null; country?: string | null; isRegionDate: boolean; earliestDate?: string | null; earliestCountry?: string | null;
  movieId?: number | null; seriesId?: number | null; episodeId?: number | null;
  title: string; year: number; episodeTitle?: string | null; seasonNumber?: number | null; episodeNumber?: number | null;
  network?: string | null; posterUrl?: string | null; overview?: string | null; runtime: number;
  monitored: boolean; hasFile: boolean; downloading: boolean;
}

export interface MovieLookup {
  tmdbId: number; imdbId?: string; title: string; originalTitle?: string; year: number; overview?: string;
  posterUrl?: string; fanartUrl?: string; runtime: number; status?: string; rating?: number; movieId?: number;
}

export interface SeriesLookup {
  tmdbId?: number; tvdbId?: number; title: string; year: number; overview?: string; posterUrl?: string; network?: string; status?: string; rating?: number; seriesId?: number;
}

export type MovieDiscoverList = 'trending' | 'popular' | 'upcoming' | 'top-rated' | 'now-playing';
export type SeriesDiscoverList = 'trending' | 'popular' | 'upcoming' | 'top-rated' | 'airing-today';
export interface DiscoverPage<T> { results: T[]; page: number; totalPages: number; }
/** A movie or show from a discovery list, recommendations or similar titles. */
export type DiscoverItem = { kind: 'movie'; item: MovieLookup } | { kind: 'series'; item: SeriesLookup };

export interface CastMember { tmdbId: number; name: string; character?: string; profileUrl?: string; episodes?: number; }
export interface CrewMember { tmdbId: number; name: string; jobs: string; profileUrl?: string; }
export interface Credits { cast: CastMember[]; crew: CrewMember[]; }
/** Any TMDB movie, in the library or not. */
export interface MovieDetails extends Credits {
  movie: MovieLookup; tagline?: string; releaseDate?: string;
  releases: MovieRelease[]; releaseRegion?: string | null;
  genres: string[];
}
export interface SeasonSummary { seasonNumber: number; name?: string; episodeCount: number; airDate?: string; posterUrl?: string; }
/** Any TMDB show, in the library or not. */
export interface SeriesDetails extends Credits {
  show: SeriesLookup; fanartUrl?: string; imdbId?: string; tagline?: string; firstAired?: string; lastAired?: string;
  runtime: number; episodeCount: number; genres: string[]; seasons: SeasonSummary[];
}
export interface PersonCredit {
  kind: 'movie' | 'series'; tmdbId: number; title: string; year: number; date?: string; overview?: string; posterUrl?: string; rating?: number;
  department: string; role?: string; movieId?: number; seriesId?: number;
}
export interface PersonDetails {
  tmdbId: number; name: string; biography?: string; birthday?: string; deathday?: string; placeOfBirth?: string; profileUrl?: string;
  knownForDepartment?: string; imdbId?: string; credits: PersonCredit[];
}
export type SeriesStatus = 'Continuing' | 'Ended' | 'Upcoming';
export type MonitorMode = 'All' | 'Future' | 'Missing' | 'FirstSeason' | 'LatestSeason' | 'None';
export interface SeasonInfo {
  seasonNumber: number; monitored: boolean; episodeCount: number; episodeFileCount: number; totalEpisodeCount: number; sizeOnDisk: number;
}
export interface Series {
  id: number; tmdbId?: number; tvdbId?: number; imdbId?: string; title: string; year: number; overview?: string; posterUrl?: string; fanartUrl?: string;
  /** Title from the metadata source; differs from `title` when the user edited it. */
  metadataTitle: string;
  network?: string; runtime: number; status: SeriesStatus; monitored: boolean; seasonFolder: boolean; qualityProfileId: number; path: string;
  added: string; episodeCount: number; episodeFileCount: number; sizeOnDisk: number; nextAiring?: string; downloadingCount: number;
  seasons: SeasonInfo[];
  /** Wanted audio languages for this series; null = the global default. */
  languages?: string[] | null;
}
/** Row of the series grid (GET /series). */
export interface SeriesListItem {
  id: number; title: string; year: number; posterUrl?: string | null; status: SeriesStatus; monitored: boolean;
  episodeCount: number; episodeFileCount: number; nextAiring?: string | null; downloadingCount: number;
}
export interface EpisodeFile { id: number; relativePath: string; size: number; quality: string; releaseGroup?: string; dateAdded: string; }
export interface Episode {
  id: number; seasonNumber: number; episodeNumber: number; absoluteNumber?: number; title?: string; overview?: string; airDate?: string; runtime: number;
  monitored: boolean; hasAired: boolean; episodeFile?: EpisodeFile; downloadProgress?: number;
}
/** TMDB's page for one library episode: still, score, guest stars and crew. */
export interface EpisodeDetails {
  showTmdbId: number; tmdbId?: number; tvdbId?: number; imdbId?: string; title?: string; overview?: string; airDate?: string; runtime: number;
  stillUrl?: string; rating?: number; voteCount: number; guestStars: CastMember[]; crew: CrewMember[];
}
export interface EpisodeRelease {
  release: ReleaseInfo; quality: string; qualityWeight: number; approved: boolean; rejections: string[]; episodes?: string; seasonPack: boolean; languages: string[];
}

/** What a bulk action on the downloads page multi-selection does. */
export type BulkDownloadAction = 'Pause' | 'Resume' | 'Remove';

export interface TorrentStatus {
  id: number; infoHash: string; name: string; state: DownloadState; progress: number; size: number; downloaded: number; uploaded: number;
  downloadRate: number; uploadRate: number; seeds: number; leechers: number; ratio: number; etaSeconds?: number;
  category: string; savePath: string; added: string; completedAt?: string; movieId?: number; seriesId?: number; imported: boolean;
  importMessage?: string; errorMessage?: string;
  /** The client that owns the torrent. */
  client: DownloadClientName;
}
/** `Download.Client` values on the server. */
export type DownloadClientName = 'embedded' | 'qbittorrent';

export interface HistoryItem {
  id: number; eventType: HistoryEventType; date: string; sourceTitle: string; movieId?: number; seriesId?: number;
  indexer?: string; message?: string; quality?: string; language?: string;
}

export interface ScheduledTaskInfo {
  id: string; name: string; intervalSeconds: number | null; lastRun: string | null; lastDurationSeconds: number | null;
  lastSucceeded: boolean | null; lastError: string | null; nextRun: string | null; isRunning: boolean;
}
export interface CommandInfo { id: string; name: string; status: CommandStatus; message?: string; queued: string; started?: string; ended?: string; }

export interface GeneralSettings { apiKey: string; instanceName: string; }
export interface OrganizeItem { episodeFileId: number; existing: string; new: string; problem?: string | null; }
export interface OrganizePreview { path: string; newPath?: string | null; files: OrganizeItem[]; }

export interface MediaManagementSettings {
  renameFiles: boolean; movieFolderFormat: string; movieFileFormat: string; useHardlinks: boolean; minimumVideoFileSizeMb: number;
  seriesFolderFormat: string; seasonFolderFormat: string; episodeFileFormat: string;
}
export type SeriesMetadataSource = 'Tmdb' | 'Tvdb';
/** Scores from OMDb; each is null when the source has none. */
export interface TitleRatings { imdb: number | null; imdbVotes: number | null; rottenTomatoes: number | null; metacritic: number | null; rottenTomatoesUrl: string | null; }
export interface MetadataSettings { tmdbApiKey?: string; seriesSource: SeriesMetadataSource; tvdbApiKey?: string; tvdbPin?: string; omdbApiKey?: string; language: string; releaseRegion?: string | null; }
export interface JellyfinSettings { url?: string | null; apiKey?: string | null; publicUrl?: string | null; }
export interface JellyfinTestResult { success: boolean; serverName?: string; version?: string; message?: string; }
export type OtlpProtocol = 'Grpc' | 'HttpProtobuf';
export interface TelemetrySettings {
  enabled: boolean; endpoint?: string | null; protocol: OtlpProtocol; headers?: string | null; serviceName: string; resourceAttributes?: string | null;
  exportTraces: boolean; exportMetrics: boolean; exportLogs: boolean; traceSampleRatio: number; metricExportIntervalSeconds: number; libraryStatsIntervalMinutes: number;
}
export interface TelemetryStatus { active: boolean; signals: string[]; endpoint?: string | null; protocol?: string | null; error?: string | null; }
export type DownloadClientKind = 'Embedded' | 'QBittorrent';
export type SeedLimitAction = 'Remove' | 'Stop';
export interface QBittorrentSettings {
  url: string; username?: string; password?: string; apiKey?: string; tag: string; category?: string; savePath?: string;
  remotePathPrefix?: string; localPathPrefix?: string;
}
export interface DownloadClientTestResult { success: boolean; version?: string; message?: string; }
/** The client new torrents go to, plus `others`: clients still running because they own downloads. */
export interface DownloadClientStatus {
  kind: DownloadClientKind; connected: boolean; error?: string; tag?: string;
  others?: { kind: DownloadClientKind; connected: boolean; error?: string }[];
}
export interface DownloadClientSettings {
  client: DownloadClientKind; qBittorrent: QBittorrentSettings;
  downloadDirectory?: string; listenPort: number; maxDownloadRateKiB: number; maxUploadRateKiB: number; maxConnections: number;
  seedRatioLimit: number; seedTimeLimitMinutes: number; seedLimitAction: SeedLimitAction; enableDht: boolean; enablePortForwarding: boolean;
}
export interface AutomationSettings { rssSyncIntervalMinutes: number; searchOnAdd: boolean; }

export interface SystemStatus {
  version: string; runtime: string; os: string; isDocker: boolean; configDirectory: string; downloadDirectory: string;
  databaseProvider: string; environmentName: string; startTime: string;
}

export interface DirectoryListing { path: string; parent?: string; directories: { name: string; path: string }[]; }

export type HearingImpairedMode = 'PreferNormal' | 'PreferHearingImpaired' | 'ExcludeHearingImpaired';
export interface SubtitleSettings {
  enabled: boolean; languages: string[]; hearingImpaired: HearingImpairedMode; minimumScoreMovies: number; minimumScoreEpisodes: number;
  searchIntervalHours: number; includeAiTranslated: boolean; useEmbeddedSubtitles: boolean;
  openSubtitles: { enabled: boolean; apiKey?: string; username?: string; password?: string };
}
export interface Language { code: string; name: string; }
/** Wanted audio languages, most preferred first; empty = any language. */
export interface LanguageSettings { preferred: string[]; }
export interface LanguageCatalog { all: Language[]; preferred: string[]; }
export interface SubtitleFile {
  id: number; movieFileId?: number; episodeFileId?: number; language: string; languageName: string; hearingImpaired: boolean; forced: boolean;
  /** provider 'embedded' = a track inside the video file (id 0, cannot be deleted). */
  fileName: string; provider: string; score?: number; release?: string; dateAdded: string;
}
export interface SubtitleCandidate {
  provider: string; payload: string; language: string; releaseNames: string[]; fileName?: string; hearingImpaired: boolean; forced: boolean;
  hashMatch: boolean; aiTranslated: boolean; trustedUploader: boolean; downloads: number;
}
export interface ScoredSubtitle { candidate: SubtitleCandidate; score: number; matches: string[]; rejections: string[]; acceptable: boolean; }
export interface WantedSubtitle {
  mediaType: 'Movie' | 'Series'; movieId?: number; seriesId?: number; movieFileId?: number; episodeFileId?: number; label: string; missingLanguages: string[];
}
/** The subtitle languages Krakarr looks for (Settings › Subtitles). */
export interface WantedSubtitleLanguages { enabled: boolean; useEmbeddedSubtitles: boolean; languages: string[]; }
export interface SubtitleFileRef { movieFileId?: number; episodeFileId?: number; }

export type MatchConfidence = 'None' | 'Low' | 'Medium' | 'High' | 'Exact';
export interface MovieImportCandidate {
  folderName: string; path: string; parsedTitle: string; parsedYear?: number; match?: MovieLookup; confidence: MatchConfidence;
  alreadyInLibrary: boolean; videoFile?: { name: string; size: number; quality: string }; videoFileCount: number; error?: string;
}
export interface SeriesImportCandidate {
  folderName: string; path: string; parsedTitle: string; parsedYear?: number; match?: SeriesLookup; confidence: MatchConfidence;
  alreadyInLibrary: boolean; videoFileCount: number; episodeFileCount: number; error?: string;
}

export interface TorrentFileDetails { index: number; path: string; size: number; progress: number; priority: string; availability?: number | null; }
export interface TorrentPeerDetails {
  address: string; client?: string; progress: number; downloadRate: number; uploadRate: number; downloaded: number; uploaded: number;
  flags?: string; flagsDescription?: string; connection?: string; country?: string; isSeeder?: boolean;
}
export interface TorrentTrackerDetails {
  url: string; tier: number; status: string; seeds?: number | null; leechers?: number | null; peers?: number | null; downloaded?: number | null;
  message?: string | null; nextAnnounceSeconds?: number | null;
}
export interface TorrentDetails {
  status: TorrentStatus; client: string; clientState?: string; infoHashV1?: string; infoHashV2?: string; magnetUri?: string; comment?: string;
  createdBy?: string; creationDate?: string; isPrivate?: boolean; pieceSize?: number; pieceCount?: number; piecesHave?: number;
  totalSize?: number; selectedSize?: number; sessionDownloaded?: number; sessionUploaded?: number; wasted?: number; availability?: number;
  timeActiveSeconds?: number; seedingTimeSeconds?: number; connections?: number; connectionsLimit?: number; seedsConnected?: number;
  seedsTotal?: number; peersConnected?: number; peersTotal?: number; downloadLimit?: number; uploadLimit?: number;
  averageDownloadRate?: number; averageUploadRate?: number; lastActivity?: string; lastSeenComplete?: string; nextAnnounceSeconds?: number;
  contentPath?: string; tags: string[]; webSeeds: string[]; files: TorrentFileDetails[]; peers: TorrentPeerDetails[];
  trackers: TorrentTrackerDetails[]; pieces?: string;
}

export type RequestStatus = 'Pending' | 'Approved' | 'Declined';
/** How much of a requested title is in the library: Missing = added but nothing downloaded yet. */
export type RequestAvailability = 'Missing' | 'Partial' | 'Available';
export interface MediaRequest {
  id: number; mediaType: MediaType; tmdbId: number; tvdbId?: number | null; title: string; year: number; posterUrl?: string | null;
  status: RequestStatus; requestedById: number; requestedBy?: string | null; requestedAt: string; processedBy?: string | null;
  processedAt?: string | null; declineReason?: string | null; libraryId?: number | null; availability?: RequestAvailability | null;
}
export interface MediaRequestPage { results: MediaRequest[]; page: number; totalPages: number; total: number; }
/** Library options a manager picks when approving a request (movie and series fields are used as relevant). */
export interface ApproveRequestOptions {
  qualityProfileId: number; rootFolderPath: string; searchNow: boolean;
  monitored?: boolean; minimumAvailability?: MinimumAvailability; monitor?: MonitorMode; seasonFolder?: boolean;
  /** Series only: title to use instead of the metadata one; blank = the metadata title. */
  title?: string;
}

export type NotificationSeverity = 'Info' | 'Warning' | 'Error';
/** An item in the top bar's bell, computed by the server from the current state (see INotificationSource). */
export interface AppNotification {
  id: string; kind: string; title: string; message?: string | null; link: string; count: number; severity: NotificationSeverity; date?: string | null;
}

/** A library title the signed-in user follows (shows in "My titles"). */
export interface Subscription { mediaType: MediaType; libraryId: number; title: string; year: number; posterUrl?: string | null; subscribedAt: string; }
/** Whether the caller follows a title; `subscribers` lists everyone who does, for staff only. */
export interface SubscriptionState { subscribed: boolean; subscribers?: string[] | null; }
/** A user who follows at least one title (staff calendar picker). */
export interface Subscriber { id: number; username: string; count: number; }

export interface DashboardStats {
  library: {
    movies: { total: number; monitored: number; downloaded: number; missing: number };
    series: { total: number; monitored: number; continuing: number; ended: number };
    episodes: { total: number; downloaded: number; missing: number; airingThisWeek: number };
    files: number; sizeOnDisk: number; subtitles: number;
    /** Resolution 0 = unknown. */
    resolutions: { resolution: number; files: number; size: number }[];
  };
  users: { total: number; admins: number; managers: number; users: number; topRequesters: { username: string; requests: number }[] };
  requests: {
    total: number; pending: number; approved: number; declined: number; last30Days: number;
    recent: { id: number; mediaType: MediaType; title: string; year: number; posterUrl?: string; status: RequestStatus; requestedBy?: string; requestedAt: string }[];
  };
  /** Last 7 days, plus downloads whose import failed. */
  activity: { grabbed: number; imported: number; failed: number; subtitles: number; importIssues: number };
  recentlyImported: { mediaType: MediaType; id: number; title: string; year: number; posterUrl?: string; date: string }[];
}
