import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Auth } from './auth';
import { readNdjson } from './ndjson';
import type { Passkey, PasskeyCreationOptionsJson, PasskeyCredentialJson, PasskeyRequestOptionsJson } from './passkeys';
import {
  AuthStatus, AutomationSettings, BackupInfo, BackupList, BackupSettings, CommandInfo, DirectoryListing, DownloadClientSettings, DownloadClientStatus, DownloadClientTestResult, GeneralSettings, HistoryItem, Indexer, SearchIndexer,
  IndexerTestResult, ProwlarrImportResult, ProwlarrListResult, SearchStreamEvent, Language, LanguageCatalog, LanguageSettings, TorrentDetails, MovieImportCandidate, SeriesImportCandidate, ScoredSubtitle, SubtitleCandidate, SubtitleFile, SubtitleFileRef, SubtitleProvider, SubtitleSettings, SubtitleSyncTools, WantedSubtitleLanguages, WantedSubtitlePage, WantedSubtitleQuery, Episode, EpisodeDetails, EpisodeRelease, MediaInfo, MediaType, MonitorMode, Series, SeriesListPage, SeriesListQuery, MediaManagementSettings, MetadataSettings, OrganizePreview, Movie, MovieCollectionDetails, MovieCollectionSummary, MovieFilter, MovieListPage, MovieListQuery, MovieLookup, Quality, QualityProfile,
  ReleaseDecision, ReleaseInfo, Credits, MovieDetails, SeriesDetails, PersonDetails, PersonLookup, KeywordLookup, CalendarEntry, MinimumAvailability, DiscoverPage, MovieDiscoverList, SeriesDiscoverList, RootFolder, ScheduledTaskInfo, SearchResult, SearchType, SeriesLookup, SystemStatus, TorrentStatus,
  DashboardStats, TitleRatings, UserAccount, UserRole, JellyfinSettings, JellyfinTestResult, TelemetrySettings, TelemetryStatus, MediaRequest, MediaRequestPage, RequestStatus, ApproveRequestOptions, AppNotification, Subscription, SubscriptionState, Subscriber, BulkResult, BulkDownloadAction, ImportIssue, DownloadMediaInfo, HealthReport, IndexerStatus
} from './models';

const API = 'api/v1';

const libraryPath = (mediaType: MediaType) => mediaType === 'Movie' ? 'movies' : 'series';

/** URL that downloads a backup (a plain link: the browser sends the session cookie). */
export const backupUrl = (name: string) => `${API}/system/backups/${encodeURIComponent(name)}`;

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

export type SettingsMap = {
  general: GeneralSettings; media: MediaManagementSettings; metadata: MetadataSettings;
  downloads: DownloadClientSettings; automation: AutomationSettings; subtitles: SubtitleSettings; languages: LanguageSettings;
  jellyfin: JellyfinSettings; telemetry: TelemetrySettings; backup: BackupSettings;
};

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);
  private readonly injector = inject(Injector);

  private static params(params?: Record<string, string | number | boolean | null | undefined>) {
    let p = new HttpParams();
    // Skip null too: `String(null)` would send "null", which the API can't bind to an int? (400).
    for (const [k, v] of Object.entries(params ?? {})) if (v != null && v !== '') p = p.set(k, String(v));
    return p;
  }

  private get<T>(url: string, params?: Record<string, string | number | boolean | null | undefined>) {
    return firstValueFrom(this.http.get<T>(`${API}/${url}`, { params: Api.params(params) }));
  }

  /**
   * GETs a streamed (newline-delimited JSON) response, calling `onEvent` per line as it arrives. Uses `fetch`, as
   * HttpClient only hands out the body at the end; failures throw an `HttpErrorResponse` (see `errorMessage`) and a
   * 401 sends the user to the login page, like the HTTP interceptor.
   */
  private async stream<T>(url: string, params: Record<string, string | number | boolean | null | undefined>,
    onEvent: (e: T) => void, signal: AbortSignal): Promise<void> {
    const query = Api.params(params).toString();
    const target = `${API}/${url}${query ? `?${query}` : ''}`;
    const res = await fetch(target, { headers: { Accept: 'application/x-ndjson' }, credentials: 'same-origin', signal });
    if (!res.ok || !res.body) {
      const error = await res.json().catch(() => null);
      if (res.status === 401) {
        this.injector.get(Auth).markLoggedOut();
        this.injector.get(Router).navigateByUrl('/login');
      }
      throw new HttpErrorResponse({ status: res.status, statusText: res.statusText, error, url: target });
    }
    await readNdjson(res.body, onEvent);
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

  // Passkeys
  passkeyLoginOptions() { return this.post<PasskeyRequestOptionsJson>('auth/passkeys/login/options'); }
  passkeyLogin(credential: PasskeyCredentialJson) { return this.post<void>('auth/passkeys/login', { credential }); }
  passkeys() { return this.get<Passkey[]>('auth/passkeys'); }
  /** Starts adding a passkey; needs the current password. */
  passkeyRegistrationOptions(currentPassword: string) {
    return this.post<PasskeyCreationOptionsJson>('auth/passkeys/register/options', { currentPassword });
  }
  addPasskey(name: string, credential: PasskeyCredentialJson) { return this.post<Passkey>('auth/passkeys', { name, credential }); }
  renamePasskey(id: number, name: string) { return this.put<Passkey>(`auth/passkeys/${id}`, { name }); }
  deletePasskey(id: number) { return this.delete<void>(`auth/passkeys/${id}`); }

  // Users (admin)
  users() { return this.get<UserAccount[]>('users'); }
  createUser(user: { username: string; password: string; role: UserRole }) { return this.post<UserAccount>('users', user); }
  updateUser(id: number, user: { username: string; role: UserRole; password?: string | null }) { return this.put<UserAccount>(`users/${id}`, user); }
  deleteUser(id: number) { return this.delete<void>(`users/${id}`); }

  // System
  systemStatus() { return this.get<SystemStatus>('system/status'); }
  commands() { return this.get<CommandInfo[]>('system/commands'); }
  tasks() { return this.get<ScheduledTaskInfo[]>('system/tasks'); }
  dashboard() { return this.get<DashboardStats>('dashboard'); }
  runTask(id: string) { return this.post<void>(`system/tasks/${encodeURIComponent(id)}/run`); }
  health() { return this.get<HealthReport>('system/health'); }
  /** Runs every health check now (API keys are validated again) and returns the new report. */
  checkHealth() { return this.post<HealthReport>('system/health/check'); }

  // Backups (admin)
  backups() { return this.get<BackupList>('system/backups'); }
  createBackup() { return this.post<BackupInfo>('system/backups'); }
  deleteBackup(name: string) { return this.delete<void>(`system/backups/${encodeURIComponent(name)}`); }
  /** Uploads a backup zip, sent as the raw body so the server streams it to disk; it is restored on the next start. */
  uploadBackup(file: Blob) {
    return firstValueFrom(this.http.post<void>(`${API}/system/backups/restore`, file, { headers: { 'Content-Type': 'application/zip' } }));
  }
  /** Restores one of the stored backups on the next start. */
  restoreBackup(name: string) { return this.post<void>(`system/backups/${encodeURIComponent(name)}/restore`); }
  cancelRestore() { return this.delete<void>('system/backups/restore'); }

  // Settings
  settings<K extends keyof SettingsMap>(section: K) { return this.get<SettingsMap[K]>(`settings/${section}`); }
  saveSettings<K extends keyof SettingsMap>(section: K, value: SettingsMap[K]) { return this.put<SettingsMap[K]>(`settings/${section}`, value); }
  telemetryStatus() { return this.get<TelemetryStatus>('settings/telemetry/status'); }
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
  /** Indexers backing off after failures. */
  indexerStatus() { return this.get<IndexerStatus[]>('indexers/status'); }
  testIndexer(i: Indexer) { return this.post<IndexerTestResult>('indexers/test', i); }
  indexerCapabilities(i: Indexer) { return this.post<IndexerTestResult>('indexers/capabilities', i); }
  prowlarrIndexers(url: string, apiKey: string) { return this.post<ProwlarrListResult>('indexers/prowlarr/list', { url, apiKey }); }
  importProwlarrIndexers(url: string, apiKey: string, indexerIds: number[]) {
    return this.post<ProwlarrImportResult>('indexers/prowlarr/import', { url, apiKey, indexerIds });
  }
  /** Enabled indexers that manual search can target. */
  searchIndexers() { return this.get<SearchIndexer[]>('search/indexers'); }
  /** Streamed: results arrive as each indexer answers (see `ReleaseSearch`). */
  searchStream(query: string, type: SearchType, onEvent: (e: SearchStreamEvent<SearchResult>) => void, signal: AbortSignal,
    indexerIds?: readonly number[]) {
    return this.stream('search', { query, type, indexerIds: indexerIds?.length ? indexerIds.join(',') : null }, onEvent, signal);
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
  bulkDownloads(hashes: string[], action: BulkDownloadAction, deleteData = false) {
    return this.post<BulkResult>('downloads/bulk', { hashes, action, deleteData });
  }
  removeDownload(hash: string, deleteData: boolean) { return this.delete<void>(`downloads/${hash}`, { deleteData }); }
  downloadMedia() { return this.get<DownloadMediaInfo[]>('downloads/media'); }
  importIssues() { return this.get<ImportIssue[]>('downloads/import-issues'); }
  retryImport(id: number) { return this.post<void>(`downloads/import-issues/${id}/retry`); }
  dismissImportIssue(id: number) { return this.delete<void>(`downloads/import-issues/${id}`); }

  // Movies
  movies(query?: MovieListQuery) { return this.get<MovieListPage>('movies', query as Record<string, string | number | boolean | null | undefined>); }
  movieIds(query?: Omit<MovieListQuery, 'page' | 'pageSize'>) {
    return this.get<number[]>('movies/ids', query as Record<string, string | number | boolean | null | undefined>);
  }
  /** Collections with library movies matching the movie list's search and filter. */
  movieCollections(query?: { q?: string; filter?: MovieFilter }) { return this.get<MovieCollectionSummary[]>('movies/collections', query); }
  movieCollection(id: number) { return this.get<MovieCollectionDetails>(`movies/collections/${id}`); }
  /** Movies of the collection that are not in the library (asks TMDB). */
  movieCollectionMissing(id: number) { return this.get<MovieLookup[]>(`movies/collections/${id}/missing`); }
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
  /** Deletes a movie's file from disk; `unmonitor` also unmonitors the movie. */
  deleteMovieFile(id: number, unmonitor = false) { return this.delete<void>(`moviefiles/${id}`, { unmonitor }); }
  refreshMovie(id: number) { return this.post<CommandInfo>(`movies/${id}/refresh`); }
  searchMovie(id: number) { return this.post<CommandInfo>(`movies/${id}/search`); }
  /** Interactive search, streamed (see `ReleaseSearch`). */
  movieReleasesStream(id: number, onEvent: (e: SearchStreamEvent<ReleaseDecision>) => void, signal: AbortSignal) {
    return this.stream(`movies/${id}/releases`, {}, onEvent, signal);
  }
  grabMovieRelease(id: number, release: ReleaseInfo) { return this.post<unknown>(`movies/${id}/releases/grab`, { release }); }
  history(movieId?: number, page = 1, pageSize = 50, seriesId?: number) {
    return this.get<{ total: number; items: HistoryItem[] }>('history', { movieId, seriesId, page, pageSize });
  }

  // Notifications (the bell): everything the signed-in user should look at.
  notifications() { return this.get<AppNotification[]>('notifications'); }
  dismissNotification(id: string) { return this.delete<void>(`notifications/${encodeURIComponent(id)}`); }
  dismissAllNotifications() { return this.delete<void>('notifications'); }

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

  /** Follows or unfollows several library titles at once. */
  bulkSubscribe(mediaType: MediaType, ids: number[], subscribed: boolean) {
    return this.post<BulkResult>('subscriptions/bulk', { mediaType, ids, subscribed });
  }

  // Bulk actions on library titles (movies or series), for the tables' multi-selection.
  bulkEdit(mediaType: MediaType, ids: number[], changes: { monitored?: boolean; qualityProfileId?: number }) {
    return this.put<BulkResult>(`${libraryPath(mediaType)}/bulk`, { ids, ...changes });
  }
  bulkSearch(mediaType: MediaType, ids: number[]) { return this.post<CommandInfo>(`${libraryPath(mediaType)}/bulk/search`, { ids }); }
  bulkRefresh(mediaType: MediaType, ids: number[]) { return this.post<CommandInfo>(`${libraryPath(mediaType)}/bulk/refresh`, { ids }); }
  bulkDelete(mediaType: MediaType, ids: number[], deleteFiles: boolean) {
    return this.post<BulkResult>(`${libraryPath(mediaType)}/bulk/delete`, { ids, deleteFiles });
  }

  // Series
  seriesList(query?: SeriesListQuery) { return this.get<SeriesListPage>('series', query as Record<string, string | number | boolean | null | undefined>); }
  seriesIds(query?: Omit<SeriesListQuery, 'page' | 'pageSize'>) {
    return this.get<number[]>('series/ids', query as Record<string, string | number | boolean | null | undefined>);
  }
  series(id: number) { return this.get<Series>(`series/${id}`); }
  /** The series' Jellyfin page; null (HTTP 204) when Jellyfin isn't set up or doesn't have it. */
  seriesJellyfinLink(id: number) { return this.get<{ url: string } | null>(`series/${id}/jellyfin`); }
  episodes(seriesId: number) { return this.get<Episode[]>(`series/${seriesId}/episodes`); }
  lookupSeries(term: string) { return this.get<SeriesLookup[]>('series/lookup', { term }); }
  addSeries(req: { tmdbId?: number; tvdbId?: number; qualityProfileId: number; rootFolderPath: string; monitor: MonitorMode; seasonFolder: boolean; searchForMissing: boolean; title?: string }) {
    return this.post<Series>('series', req);
  }
  discoverSeries(list: SeriesDiscoverList, page = 1) { return this.get<DiscoverPage<SeriesLookup>>(`series/discover/${list}`, { page }); }
  seriesForYou() { return this.get<SeriesLookup[]>('series/discover/for-you'); }
  seriesRecommendations(id: number) { return this.get<SeriesLookup[]>(`series/${id}/recommendations`); }
  similarSeries(id: number) { return this.get<SeriesLookup[]>(`series/${id}/similar`); }
  seriesCredits(id: number) { return this.get<Credits>(`series/${id}/credits`); }
  /** TMDB details of a library episode; null when TMDB doesn't know it. */
  episodeDetails(seriesId: number, episodeId: number) { return this.get<EpisodeDetails | null>(`series/${seriesId}/episodes/${episodeId}/details`); }
  /** Media info read from an episode file; null when it hasn't been read (or couldn't be). */
  episodeFileMediaInfo(seriesId: number, fileId: number) { return this.get<MediaInfo | null>(`series/${seriesId}/episodefiles/${fileId}/mediainfo`); }
  /** Any TMDB show, whether or not it is in the library. */
  tmdbSeries(tmdbId: number) { return this.get<SeriesDetails>(`series/tmdb/${tmdbId}`); }
  tmdbSeriesRecommendations(tmdbId: number) { return this.get<SeriesLookup[]>(`series/tmdb/${tmdbId}/recommendations`); }
  tmdbSimilarSeries(tmdbId: number) { return this.get<SeriesLookup[]>(`series/tmdb/${tmdbId}/similar`); }

  // People
  lookupPeople(term: string) { return this.get<PersonLookup[]>('people/lookup', { term }); }

  // Keywords
  lookupKeywords(term: string) { return this.get<KeywordLookup[]>('keywords/lookup', { term }); }
  keyword(id: number) { return this.get<KeywordLookup>(`keywords/${id}`); }
  /** Movies tagged with every one of the keywords, most popular first. */
  moviesByKeywords(ids: readonly number[], page = 1) { return this.get<DiscoverPage<MovieLookup>>('movies/discover/keywords', { keywords: ids.join(','), page }); }
  /** Shows tagged with every one of the keywords, most popular first. */
  seriesByKeywords(ids: readonly number[], page = 1) { return this.get<DiscoverPage<SeriesLookup>>('series/discover/keywords', { keywords: ids.join(','), page }); }
  person(tmdbId: number) { return this.get<PersonDetails>(`people/${tmdbId}`); }
  /** IMDb, Rotten Tomatoes and Metacritic scores; null (HTTP 204) when no OMDb key is set or they are unavailable. */
  ratings(imdbId: string) { return this.get<TitleRatings | null>(`ratings/${encodeURIComponent(imdbId)}`); }
  /** Replaces the editable fields; `languages` null = use the global default. */
  /** `title`: undefined keeps the current title, blank restores the metadata title. */
  updateSeries(id: number, monitored: boolean, qualityProfileId: number, seasonFolder: boolean, languages: string[] | null, title?: string) {
    return this.put<void>(`series/${id}`, { monitored, qualityProfileId, seasonFolder, languages, title });
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
  /** Interactive search for a season or an episode, streamed (see `ReleaseSearch`). */
  seriesReleasesStream(id: number, season: number | undefined, episodeId: number | undefined,
    onEvent: (e: SearchStreamEvent<EpisodeRelease>) => void, signal: AbortSignal) {
    return this.stream(`series/${id}/releases`, { season, episodeId }, onEvent, signal);
  }
  grabSeriesRelease(id: number, release: ReleaseInfo) { return this.post<unknown>(`series/${id}/releases/grab`, { release }); }
  /** Deletes an episode file from disk; `unmonitor` also unmonitors its episodes. */
  deleteEpisodeFile(id: number, unmonitor = false) { return this.delete<void>(`episodefiles/${id}`, { unmonitor }); }
  /** Deletes several episode files from disk (a season's); `unmonitor` also unmonitors their episodes. */
  deleteEpisodeFiles(ids: number[], unmonitor = false) { return this.post<BulkResult>('episodefiles/bulk/delete', { ids, unmonitor }); }

  // Calendar
  /** Library releases between two ISO dates (inclusive); `user` = 'me' or a user id limits them to that user's titles. */
  calendar(start: string, end: string, user?: 'me' | number | null) { return this.get<CalendarEntry[]>('calendar', { start, end, user }); }

  // Subtitles
  /** Audio languages releases can be filtered by, plus the global wanted ones. */
  languages() { return this.get<LanguageCatalog>('languages'); }
  subtitleLanguages() { return this.get<Language[]>('subtitles/languages'); }
  subtitleProviders() { return this.get<SubtitleProvider[]>('subtitles/providers'); }
  testSubtitleProviders(settings: SubtitleSettings) {
    return this.post<{ name: string; success: boolean; message: string }[]>('subtitles/providers/test', settings);
  }
  subtitleFiles(filter: { movieId?: number; seriesId?: number }) { return this.get<SubtitleFile[]>('subtitles/files', filter); }
  wantedSubtitleLanguages() { return this.get<WantedSubtitleLanguages>('subtitles/wanted-languages'); }
  /** One page of the files missing a wanted language, sorted on the server, with the totals per filter. */
  wantedSubtitles(query: WantedSubtitleQuery & { page: number; pageSize: number }) {
    return this.get<WantedSubtitlePage>('subtitles/wanted', query as Record<string, string | number | boolean | null | undefined>);
  }
  /** Lets the scheduled search try a movie or episode file marked "not found" again (every marked file when omitted). */
  retrySubtitles(ref: SubtitleFileRef = {}) {
    return this.post<{ count: number }>('subtitles/retry', { movieFileId: ref.movieFileId ?? null, episodeFileId: ref.episodeFileId ?? null });
  }
  /** Marks every movie and episode file missing a wanted language "not found": the scheduled search stops retrying them. */
  markAllSubtitlesNotFound() { return this.post<{ count: number }>('subtitles/not-found', {}); }
  /** Searches one file, a whole movie or series (every wanted language, upgrading existing ones), or all wanted files when empty. */
  searchSubtitles(ref: SubtitleFileRef & { movieId?: number; seriesId?: number; providers?: string[]; notFound?: boolean } = {}) {
    return this.post<CommandInfo>('subtitles/search', {
      movieFileId: ref.movieFileId ?? null, episodeFileId: ref.episodeFileId ?? null, movieId: ref.movieId ?? null, seriesId: ref.seriesId ?? null,
      ...(ref.providers?.length ? { providers: ref.providers } : {}),
      ...(ref.notFound != null ? { notFound: ref.notFound } : {}),
    });
  }
  subtitleCandidates(ref: SubtitleFileRef, language?: string) {
    return this.get<{ label: string; results: ScoredSubtitle[]; errors: string[] }>('subtitles/candidates', { ...ref, language });
  }
  downloadSubtitle(ref: SubtitleFileRef, candidate: SubtitleCandidate, score: number) {
    return this.post<SubtitleFile>('subtitles/download', { ...ref, candidate, score });
  }
  deleteSubtitle(id: number) { return this.delete<void>(`subtitles/${id}`); }
  /** Resyncs a subtitle against the video in the background; the result lands on the subtitle (syncNote). */
  syncSubtitle(id: number) { return this.post<CommandInfo>(`subtitles/${id}/sync`, {}); }
  /** Moves every cue by offsetMs (negative = earlier). */
  shiftSubtitle(id: number, offsetMs: number) { return this.post<SubtitleFile>(`subtitles/${id}/shift`, { offsetMs }); }
  subtitleSyncTools() { return this.get<SubtitleSyncTools>('subtitles/sync/tools'); }

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
