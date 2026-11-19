export type MediaType = 'Movie' | 'Series';
export type DownloadState = 'Queued' | 'FetchingMetadata' | 'Downloading' | 'Paused' | 'Seeding' | 'Completed' | 'Error';
export type MovieStatus = 'Announced' | 'InCinemas' | 'Released';
export type HistoryEventType = 'Grabbed' | 'DownloadFailed' | 'Imported' | 'FileDeleted';
export type CommandStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';
export type SearchType = 'Generic' | 'Movie' | 'Tv';

export interface AuthStatus { setupRequired: boolean; authenticated: boolean; username?: string; }

export interface Quality { id: number; name: string; source: string; resolution: number; weight: number; }

export interface QualityProfile {
  id: number; name: string; allowedQualityIds: number[]; cutoffQualityId: number;
  upgradeAllowed: boolean; minSizeMb: number; maxSizeMb: number;
}

export interface RootFolder { id: number; path: string; mediaType: MediaType; accessible: boolean; freeSpace?: number; }

export interface Indexer {
  id: number; name: string; protocol: 'Torznab' | 'Newznab'; baseUrl: string; apiPath: string; apiKey?: string | null;
  movieCategories: number[]; tvCategories: number[]; enabled: boolean; enableRss: boolean; priority: number; minimumSeeders: number;
}

export interface IndexerCapabilities {
  searchAvailable: boolean; movieSearchAvailable: boolean; movieSearchParams: string[];
  tvSearchAvailable: boolean; tvSearchParams: string[]; categories: { id: number; name: string }[];
}

export interface IndexerTestResult { success: boolean; message?: string; capabilities?: IndexerCapabilities; }
export interface IndexerError { indexerId: number; indexerName: string; message: string; }

export interface ReleaseInfo {
  title: string; guid: string; downloadUrl?: string; magnetUrl?: string; infoHash?: string; infoUrl?: string;
  size: number; seeders?: number; peers?: number; publishDate?: string; imdbId?: number; tmdbId?: number; tvdbId?: number;
  categories: number[]; indexerId: number; indexerName: string; indexerPriority: number;
}

export interface SearchResult { release: ReleaseInfo; quality: string; parsedTitle?: string; parsedYear?: number; }
export interface ReleaseDecision { release: ReleaseInfo; quality: string; qualityWeight: number; approved: boolean; rejections: string[]; }

export interface MovieFile { id: number; relativePath: string; size: number; quality: string; releaseGroup?: string; dateAdded: string; }

export interface Movie {
  id: number; tmdbId: number; imdbId?: string; title: string; originalTitle?: string; year: number; overview?: string;
  posterUrl?: string; fanartUrl?: string; runtime: number; status: MovieStatus; monitored: boolean; qualityProfileId: number;
  path: string; added: string; hasFile: boolean; movieFile?: MovieFile; downloadProgress?: number;
}

export interface MovieLookup {
  tmdbId: number; imdbId?: string; title: string; originalTitle?: string; year: number; overview?: string;
  posterUrl?: string; fanartUrl?: string; runtime: number; status?: string; movieId?: number;
}

export interface SeriesLookup {
  tvdbId: number; title: string; year: number; overview?: string; posterUrl?: string; network?: string; status?: string; seriesId?: number;
}
export type SeriesStatus = 'Continuing' | 'Ended' | 'Upcoming';
export type MonitorMode = 'All' | 'Future' | 'Missing' | 'FirstSeason' | 'LatestSeason' | 'None';
export interface SeasonInfo {
  seasonNumber: number; monitored: boolean; episodeCount: number; episodeFileCount: number; totalEpisodeCount: number; sizeOnDisk: number;
}
export interface Series {
  id: number; tvdbId: number; imdbId?: string; title: string; year: number; overview?: string; posterUrl?: string; fanartUrl?: string;
  network?: string; runtime: number; status: SeriesStatus; monitored: boolean; seasonFolder: boolean; qualityProfileId: number; path: string;
  added: string; episodeCount: number; episodeFileCount: number; sizeOnDisk: number; nextAiring?: string; downloadingCount: number;
  seasons: SeasonInfo[];
}
export interface EpisodeFile { id: number; relativePath: string; size: number; quality: string; releaseGroup?: string; dateAdded: string; }
export interface Episode {
  id: number; seasonNumber: number; episodeNumber: number; absoluteNumber?: number; title?: string; overview?: string; airDate?: string;
  monitored: boolean; hasAired: boolean; episodeFile?: EpisodeFile; downloadProgress?: number;
}
export interface EpisodeRelease {
  release: ReleaseInfo; quality: string; qualityWeight: number; approved: boolean; rejections: string[]; episodes?: string; seasonPack: boolean;
}

export interface TorrentStatus {
  id: number; infoHash: string; name: string; state: DownloadState; progress: number; size: number; downloaded: number; uploaded: number;
  downloadRate: number; uploadRate: number; seeds: number; leechers: number; ratio: number; etaSeconds?: number;
  category: string; savePath: string; added: string; completedAt?: string; movieId?: number; seriesId?: number; imported: boolean;
  importMessage?: string; errorMessage?: string;
}

export interface HistoryItem {
  id: number; eventType: HistoryEventType; date: string; sourceTitle: string; movieId?: number; seriesId?: number;
  indexer?: string; message?: string; quality?: string;
}

export interface CommandInfo { id: string; name: string; status: CommandStatus; message?: string; queued: string; started?: string; ended?: string; }

export interface GeneralSettings { apiKey: string; instanceName: string; }
export interface MediaManagementSettings {
  renameFiles: boolean; movieFolderFormat: string; movieFileFormat: string; useHardlinks: boolean; minimumVideoFileSizeMb: number;
  seriesFolderFormat: string; seasonFolderFormat: string; episodeFileFormat: string;
}
export interface MetadataSettings { tmdbApiKey?: string; tvdbApiKey?: string; tvdbPin?: string; language: string; }
export type DownloadClientKind = 'Embedded' | 'QBittorrent';
export interface QBittorrentSettings {
  url: string; username?: string; password?: string; tag: string; category?: string; savePath?: string;
  remotePathPrefix?: string; localPathPrefix?: string;
}
export interface DownloadClientTestResult { success: boolean; version?: string; message?: string; }
export interface DownloadClientStatus { kind: DownloadClientKind; connected: boolean; error?: string; tag?: string; }
export interface DownloadClientSettings {
  client: DownloadClientKind; qBittorrent: QBittorrentSettings;
  downloadDirectory?: string; listenPort: number; maxDownloadRateKiB: number; maxUploadRateKiB: number; maxConnections: number;
  seedRatioLimit: number; seedTimeLimitMinutes: number; enableDht: boolean; enablePortForwarding: boolean;
}
export interface AutomationSettings { rssSyncIntervalMinutes: number; searchOnAdd: boolean; }

export interface SystemStatus {
  version: string; runtime: string; os: string; isDocker: boolean; configDirectory: string; downloadDirectory: string;
  databaseProvider: string; environmentName: string; startTime: string;
}

export interface DirectoryListing { path: string; parent?: string; directories: { name: string; path: string }[]; }
