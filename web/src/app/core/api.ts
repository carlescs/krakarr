import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AuthStatus, AutomationSettings, CommandInfo, DirectoryListing, DownloadClientSettings, DownloadClientStatus, DownloadClientTestResult, GeneralSettings, HistoryItem, Indexer,
  IndexerError, IndexerTestResult, Language, LanguageCatalog, LanguageSettings, TorrentDetails, MovieImportCandidate, SeriesImportCandidate, ScoredSubtitle, SubtitleCandidate, SubtitleFile, SubtitleFileRef, SubtitleSettings, WantedSubtitle, Episode, EpisodeRelease, MediaType, MonitorMode, Series, SeriesListItem, MediaManagementSettings, MetadataSettings, OrganizePreview, Movie, MovieCollection, MovieListItem, MovieLookup, Quality, QualityProfile,
  ReleaseDecision, ReleaseInfo, Credits, MovieDetails, SeriesDetails, PersonDetails, CalendarEntry, MinimumAvailability, DiscoverPage, MovieDiscoverList, SeriesDiscoverList, RootFolder, ScheduledTaskInfo, SearchResult, SearchType, SeriesLookup, SystemStatus, TorrentStatus,
  UserAccount, UserRole, JellyfinSettings, JellyfinTestResult, MediaRequest, MediaRequestPage, RequestStatus, ApproveRequestOptions, AppNotification, Subscription, SubscriptionState, Subscriber,
} from './models';

const API = 'api/v1';

/** Optional library item a manual download is imported into. */
export interface DownloadLink { movieId?: number; seriesId?: number; }

/** Extracts a human readable message from an API error (ProblemDetails / ValidationProblem). */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error;
    if (body?.errors) {
      return Object.values(body.errors as Record<string, string[]>).flat().join(' ');
    }
    if (body?.detail) return body.detail;
    if (body?.title) return body.title;
    if (err.status === 0) return 'Cannot reach the Krakarr server';
    if (err.status === 429) return 'Too many attempts. Please wait a minute and try again.';
    return `${err.status} ${err.statusText}`;
  }
  return err instanceof Error ? err.message : String(err);
}

type SettingsMap = {
  general: GeneralSettings; media: MediaManagementSettings; metadata: MetadataSettings;
  downloads: DownloadClientSettings; automation: AutomationSettings; subtitles: SubtitleSettings; languages: LanguageSettings;
  jellyfin: JellyfinSettings;
};

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  private get<T>(url: string, params?: Record<string, string | number | boolean | null | undefined>) {
    let p = new HttpParams();
    // Skip null too: `String(null)` would send "null", which the API can't bind to an int? (400).
    for (const [k, v] of Object.entries(params ?? {})) if (v != null && v !== '') p = p.set(k, String(v));
    return firstValueFrom(this.http.get<T>(`${API}/${url}`, { params: p }));
  }
  private post<T>(url: string, body: unknown = {}) { return firstValueFrom(this.http.post<T>(`${API}/${url}`, body)); }
  private put<T>(url: string, body: unknown) { return firstValueFrom(this.http.put<T>(`${API}/${url}`, body)); }
  private delete<T>(url: string, params?: Record<string, string | boolean>) {
    return firstValueFrom(this.http.delete<T>(`${API}/${url}`, { params }));
  }

  // Auth
  authStatus() { return this.get<AuthStatus>('auth/status'); }
  login(username: string, password: string) { return this.post<void>('auth/login', { username, password }); }
  setup(username: string, password: string, setupToken: string) { return this.post<void>('auth/setup', { username, password, setupToken }); }
  logout() { return this.post<void>('auth/logout'); }
  /** Signs out every other browser and device of the current user; this session stays signed in. */
  revokeOtherSessions() { return this.post<void>('auth/sessions/revoke'); }
  changeCredentials(username: string, currentPassword: string, newPassword?: string) {
    return this.post<void>('auth/password', { username, currentPassword, newPassword: newPassword || null });
  }

  // Users (admin)
  users() { return this.get<UserAccount[]>('users'); }
  createUser(user: { username: string; password: string; role: UserRole }) { return this.post<UserAccount>('users', user); }
  updateUser(id: number, user: { username: string; role: UserRole; password?: string | null }) { return this.put<UserAccount>(`users/${id}`, user); }
  deleteUser(id: number) { return this.delete<void>(`users/${id}`); }

  // System
  systemStatus() { return this.get<SystemStatus>('system/status'); }
  commands() { return this.get<CommandInfo[]>('system/commands'); }
  tasks() { return this.get<ScheduledTaskInfo[]>('system/tasks'); }
  runTask(id: string) { return this.post<void>(`system/tasks/${encodeURIComponent(id)}/run`); }

  // Settings
  settings<K extends keyof SettingsMap>(section: K) { return this.get<SettingsMap[K]>(`settings/${section}`); }
  saveSettings<K extends keyof SettingsMap>(section: K, value: SettingsMap[K]) { return this.put<SettingsMap[K]>(`settings/${section}`, value); }
  testJellyfin(settings: JellyfinSettings) { return this.post<JellyfinTestResult>('settings/jellyfin/test', settings); }
  regenerateApiKey() { return this.post<GeneralSettings>('settings/general/regenerate-api-key'); }
  qualities() { return this.get<Quality[]>('qualities'); }
  qualityProfiles() { return this.get<QualityProfile[]>('qualityprofiles'); }
  saveQualityProfile(p: QualityProfile) { return p.id ? this.put<QualityProfile>(`qualityprofiles/${p.id}`, p) : this.post<QualityProfile>('qualityprofiles', p); }
  deleteQualityProfile(id: number) { return this.delete<void>(`qualityprofiles/${id}`); }
  rootFolders() { return this.get<RootFolder[]>('rootfolders'); }
  addRootFolder(path: string, mediaType: MediaType = 'Movie') { return this.post<RootFolder>('rootfolders', { path, mediaType }); }
  deleteRootFolder(id: number) { return this.delete<void>(`rootfolders/${id}`); }
  browse(path?: string) { return this.get<DirectoryListing>('filesystem', { path }); }

  // Indexers
  indexers() { return this.get<Indexer[]>('indexers'); }
  saveIndexer(i: Indexer) { return i.id ? this.put<Indexer>(`indexers/${i.id}`, i) : this.post<Indexer>('indexers', i); }
  deleteIndexer(id: number) { return this.delete<void>(`indexers/${id}`); }
  testIndexer(i: Indexer) { return this.post<IndexerTestResult>('indexers/test', i); }
  indexerCapabilities(i: Indexer) { return this.post<IndexerTestResult>('indexers/capabilities', i); }
  search(query: string, type: SearchType) {
    return this.get<{ results: SearchResult[]; errors: IndexerError[] }>('search', { query, type });
  }

  // Downloads
  downloads() { return this.get<TorrentStatus[]>('downloads'); }
  downloadClientStatus() { return this.get<DownloadClientStatus>('downloads/client'); }
  testDownloadClient(settings: DownloadClientSettings) { return this.post<DownloadClientTestResult>('downloads/client/test', settings); }
  addMagnet(magnet: string, category?: string, link: DownloadLink = {}) { return this.post<unknown>('downloads/magnet', { magnet, category, ...link }); }
  addTorrentFile(file: File, category?: string, link: DownloadLink = {}) {
    const form = new FormData();
    form.append('file', file);
    if (category) form.append('category', category);
    if (link.movieId != null) form.append('movieId', String(link.movieId));
    if (link.seriesId != null) form.append('seriesId', String(link.seriesId));
    return firstValueFrom(this.http.post(`${API}/downloads/torrent`, form));
  }
  grabRelease(release: ReleaseInfo, category?: string, link: DownloadLink = {}) { return this.post<unknown>('downloads/release', { release, category, ...link }); }
  pauseDownload(hash: string) { return this.post<void>(`downloads/${hash}/pause`); }
  resumeDownload(hash: string) { return this.post<void>(`downloads/${hash}/resume`); }
  downloadDetails(hash: string) { return this.get<TorrentDetails>(`downloads/${hash}/details`); }
  removeDownload(hash: string, deleteData: boolean) { return this.delete<void>(`downloads/${hash}`, { deleteData }); }

  // Movies
  movies() { return this.get<MovieListItem[]>('movies'); }
  movieCollections() { return this.get<MovieCollection[]>('movies/collections'); }
  movie(id: number) { return this.get<Movie>(`movies/${id}`); }
  /** The movie's Jellyfin page; null (HTTP 204) when Jellyfin isn't set up or doesn't have it. */
  movieJellyfinLink(id: number) { return this.get<{ url: string } | null>(`movies/${id}/jellyfin`); }
  lookupMovies(term: string) { return this.get<MovieLookup[]>('movies/lookup', { term }); }
  addMovie(req: { tmdbId: number; qualityProfileId: number; rootFolderPath: string; monitored: boolean; searchForMovie: boolean; minimumAvailability: MinimumAvailability }) {
    return this.post<Movie>('movies', req);
  }
  discoverMovies(list: MovieDiscoverList, page = 1) { return this.get<DiscoverPage<MovieLookup>>(`movies/discover/${list}`, { page }); }
  moviesForYou() { return this.get<MovieLookup[]>('movies/discover/for-you'); }
  movieRecommendations(id: number) { return this.get<MovieLookup[]>(`movies/${id}/recommendations`); }
  similarMovies(id: number) { return this.get<MovieLookup[]>(`movies/${id}/similar`); }
  movieCredits(id: number) { return this.get<Credits>(`movies/${id}/credits`); }
  /** Any TMDB movie, whether or not it is in the library. */
  tmdbMovie(tmdbId: number) { return this.get<MovieDetails>(`movies/tmdb/${tmdbId}`); }
  tmdbMovieRecommendations(tmdbId: number) { return this.get<MovieLookup[]>(`movies/tmdb/${tmdbId}/recommendations`); }
  tmdbSimilarMovies(tmdbId: number) { return this.get<MovieLookup[]>(`movies/tmdb/${tmdbId}/similar`); }
  /** Replaces the editable fields; `languages` null = use the global default. */
  updateMovie(id: number, monitored: boolean, qualityProfileId: number, minimumAvailability: MinimumAvailability | undefined, languages: string[] | null) {
    return this.put<void>(`movies/${id}`, { monitored, qualityProfileId, minimumAvailability: minimumAvailability ?? null, languages });
  }
  deleteMovie(id: number, deleteFiles: boolean) { return this.delete<void>(`movies/${id}`, { deleteFiles }); }
  searchMovie(id: number) { return this.post<CommandInfo>(`movies/${id}/search`); }
  movieReleases(id: number) { return this.get<{ releases: ReleaseDecision[]; errors: IndexerError[] }>(`movies/${id}/releases`); }
  grabMovieRelease(id: number, release: ReleaseInfo) { return this.post<unknown>(`movies/${id}/releases/grab`, { release }); }
  history(movieId?: number, page = 1, pageSize = 50, seriesId?: number) {
    return this.get<{ total: number; items: HistoryItem[] }>('history', { movieId, seriesId, page, pageSize });
  }

  // Notifications (the bell): everything the signed-in user should look at.
  notifications() { return this.get<AppNotification[]>('notifications'); }

  // Requests: users ask for titles, staff approve (adds them) or decline.
  requests(status?: RequestStatus, page = 1) { return this.get<MediaRequestPage>('requests', { status, page }); }
  /** The open request for a TMDB title, or null. */
  requestFor(mediaType: MediaType, tmdbId: number) { return this.get<MediaRequest | null>(`requests/tmdb/${mediaType}/${tmdbId}`); }
  createRequest(mediaType: MediaType, tmdbId: number) { return this.post<MediaRequest>('requests', { mediaType, tmdbId }); }
  approveRequest(id: number, options: ApproveRequestOptions) { return this.post<MediaRequest>(`requests/${id}/approve`, options); }
  declineRequest(id: number, reason?: string) { return this.post<MediaRequest>(`requests/${id}/decline`, { reason: reason || null }); }
  deleteRequest(id: number) { return this.delete<void>(`requests/${id}`); }
  /** The caller has seen how their requests were handled: clears the approved/declined notifications. */
  markRequestsSeen() { return this.post<void>('requests/seen'); }

  // Subscriptions: library titles a user follows (calendar and list "My titles").
  subscriptions() { return this.get<Subscription[]>('subscriptions'); }
  subscriptionState(mediaType: MediaType, id: number) { return this.get<SubscriptionState>(`subscriptions/${mediaType}/${id}`); }
  subscribe(mediaType: MediaType, id: number) { return this.put<void>(`subscriptions/${mediaType}/${id}`, {}); }
  unsubscribe(mediaType: MediaType, id: number) { return this.delete<void>(`subscriptions/${mediaType}/${id}`); }
  /** Staff: users who follow at least one title. */
  subscribers() { return this.get<Subscriber[]>('subscriptions/users'); }

  // Series
  seriesList() { return this.get<SeriesListItem[]>('series'); }
  series(id: number) { return this.get<Series>(`series/${id}`); }
  /** The series' Jellyfin page; null (HTTP 204) when Jellyfin isn't set up or doesn't have it. */
  seriesJellyfinLink(id: number) { return this.get<{ url: string } | null>(`series/${id}/jellyfin`); }
  episodes(seriesId: number) { return this.get<Episode[]>(`series/${seriesId}/episodes`); }
  lookupSeries(term: string) { return this.get<SeriesLookup[]>('series/lookup', { term }); }
  addSeries(req: { tmdbId?: number; tvdbId?: number; qualityProfileId: number; rootFolderPath: string; monitor: MonitorMode; seasonFolder: boolean; searchForMissing: boolean }) {
    return this.post<Series>('series', req);
  }
  discoverSeries(list: SeriesDiscoverList, page = 1) { return this.get<DiscoverPage<SeriesLookup>>(`series/discover/${list}`, { page }); }
  seriesForYou() { return this.get<SeriesLookup[]>('series/discover/for-you'); }
  seriesRecommendations(id: number) { return this.get<SeriesLookup[]>(`series/${id}/recommendations`); }
  similarSeries(id: number) { return this.get<SeriesLookup[]>(`series/${id}/similar`); }
  seriesCredits(id: number) { return this.get<Credits>(`series/${id}/credits`); }
  /** Any TMDB show, whether or not it is in the library. */
  tmdbSeries(tmdbId: number) { return this.get<SeriesDetails>(`series/tmdb/${tmdbId}`); }
  tmdbSeriesRecommendations(tmdbId: number) { return this.get<SeriesLookup[]>(`series/tmdb/${tmdbId}/recommendations`); }
  tmdbSimilarSeries(tmdbId: number) { return this.get<SeriesLookup[]>(`series/tmdb/${tmdbId}/similar`); }

  // People
  person(tmdbId: number) { return this.get<PersonDetails>(`people/${tmdbId}`); }
  /** Replaces the editable fields; `languages` null = use the global default. */
  updateSeries(id: number, monitored: boolean, qualityProfileId: number, seasonFolder: boolean, languages: string[] | null) {
    return this.put<void>(`series/${id}`, { monitored, qualityProfileId, seasonFolder, languages });
  }
  deleteSeries(id: number, deleteFiles: boolean) { return this.delete<void>(`series/${id}`, { deleteFiles }); }
  monitorSeason(seriesId: number, season: number, monitored: boolean) { return this.put<void>(`series/${seriesId}/seasons/${season}`, { monitored }); }
  monitorEpisode(id: number, monitored: boolean) { return this.put<void>(`episodes/${id}`, { monitored }); }
  refreshSeries(id: number) { return this.post<CommandInfo>(`series/${id}/refresh`); }
  organizePreview(id: number) { return this.get<OrganizePreview>(`series/${id}/organize`); }
  organizeSeries(id: number, renameFolder: boolean) { return this.post<CommandInfo>(`series/${id}/organize`, { renameFolder }); }
  searchSeries(id: number, seasonNumber?: number, episodeIds?: number[]) {
    return this.post<CommandInfo>(`series/${id}/search`, { seasonNumber: seasonNumber ?? null, episodeIds: episodeIds ?? null });
  }
  seriesReleases(id: number, season?: number, episodeId?: number) {
    return this.get<{ releases: EpisodeRelease[]; errors: IndexerError[] }>(`series/${id}/releases`, { season, episodeId });
  }
  grabSeriesRelease(id: number, release: ReleaseInfo) { return this.post<unknown>(`series/${id}/releases/grab`, { release }); }
  deleteEpisodeFile(id: number) { return this.delete<void>(`episodefiles/${id}`); }

  // Calendar
  /** Library releases between two ISO dates (inclusive); `user` = 'me' or a user id limits them to that user's titles. */
  calendar(start: string, end: string, user?: 'me' | number | null) { return this.get<CalendarEntry[]>('calendar', { start, end, user }); }

  // Subtitles
  /** Audio languages releases can be filtered by, plus the global wanted ones. */
  languages() { return this.get<LanguageCatalog>('languages'); }
  subtitleLanguages() { return this.get<Language[]>('subtitles/languages'); }
  testSubtitleProviders(settings: SubtitleSettings) {
    return this.post<{ name: string; success: boolean; message: string }[]>('subtitles/providers/test', settings);
  }
  subtitleFiles(filter: { movieId?: number; seriesId?: number }) { return this.get<SubtitleFile[]>('subtitles/files', filter); }
  wantedSubtitles() { return this.get<WantedSubtitle[]>('subtitles/wanted'); }
  /** Searches one file, a whole movie or series (every wanted language, upgrading existing ones), or all wanted files when empty. */
  searchSubtitles(ref: SubtitleFileRef & { movieId?: number; seriesId?: number } = {}) {
    return this.post<CommandInfo>('subtitles/search', {
      movieFileId: ref.movieFileId ?? null, episodeFileId: ref.episodeFileId ?? null, movieId: ref.movieId ?? null, seriesId: ref.seriesId ?? null,
    });
  }
  subtitleCandidates(ref: SubtitleFileRef, language?: string) {
    return this.get<{ label: string; results: ScoredSubtitle[]; errors: string[] }>('subtitles/candidates', { ...ref, language });
  }
  downloadSubtitle(ref: SubtitleFileRef, candidate: SubtitleCandidate, score: number) {
    return this.post<SubtitleFile>('subtitles/download', { ...ref, candidate, score });
  }
  deleteSubtitle(id: number) { return this.delete<void>(`subtitles/${id}`); }

  // Library import
  scanMovieImport(rootFolderId: number) { return this.get<MovieImportCandidate[]>('movies/import/scan', { rootFolderId }); }
  importMovies(items: { path: string; tmdbId: number; qualityProfileId: number; monitored: boolean; searchForMovie: boolean; minimumAvailability: MinimumAvailability }[]) {
    return this.post<CommandInfo>('movies/import', items);
  }
  scanSeriesImport(rootFolderId: number) { return this.get<SeriesImportCandidate[]>('series/import/scan', { rootFolderId }); }
  importSeries(items: { path: string; tmdbId?: number; tvdbId?: number; qualityProfileId: number; monitor: MonitorMode; searchForMissing: boolean }[]) {
    return this.post<CommandInfo>('series/import', items);
  }
}
