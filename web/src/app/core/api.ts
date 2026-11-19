import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AuthStatus, AutomationSettings, CommandInfo, DirectoryListing, DownloadClientSettings, GeneralSettings, HistoryItem, Indexer,
  IndexerError, IndexerTestResult, MediaManagementSettings, MetadataSettings, Movie, MovieLookup, Quality, QualityProfile,
  ReleaseDecision, ReleaseInfo, RootFolder, SearchResult, SearchType, SeriesLookup, SystemStatus, TorrentStatus,
} from './models';

const API = 'api/v1';

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
    return `${err.status} ${err.statusText}`;
  }
  return err instanceof Error ? err.message : String(err);
}

type SettingsMap = {
  general: GeneralSettings; media: MediaManagementSettings; metadata: MetadataSettings;
  downloads: DownloadClientSettings; automation: AutomationSettings;
};

@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  private get<T>(url: string, params?: Record<string, string | number | boolean | undefined>) {
    let p = new HttpParams();
    for (const [k, v] of Object.entries(params ?? {})) if (v !== undefined && v !== '') p = p.set(k, String(v));
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
  setup(username: string, password: string) { return this.post<void>('auth/setup', { username, password }); }
  logout() { return this.post<void>('auth/logout'); }
  changeCredentials(username: string, password: string) { return this.post<void>('auth/password', { username, password }); }

  // System
  systemStatus() { return this.get<SystemStatus>('system/status'); }
  commands() { return this.get<CommandInfo[]>('system/commands'); }

  // Settings
  settings<K extends keyof SettingsMap>(section: K) { return this.get<SettingsMap[K]>(`settings/${section}`); }
  saveSettings<K extends keyof SettingsMap>(section: K, value: SettingsMap[K]) { return this.put<SettingsMap[K]>(`settings/${section}`, value); }
  regenerateApiKey() { return this.post<GeneralSettings>('settings/general/regenerate-api-key'); }
  qualities() { return this.get<Quality[]>('qualities'); }
  qualityProfiles() { return this.get<QualityProfile[]>('qualityprofiles'); }
  saveQualityProfile(p: QualityProfile) { return p.id ? this.put<QualityProfile>(`qualityprofiles/${p.id}`, p) : this.post<QualityProfile>('qualityprofiles', p); }
  deleteQualityProfile(id: number) { return this.delete<void>(`qualityprofiles/${id}`); }
  rootFolders() { return this.get<RootFolder[]>('rootfolders'); }
  addRootFolder(path: string) { return this.post<RootFolder>('rootfolders', { path, mediaType: 'Movie' }); }
  deleteRootFolder(id: number) { return this.delete<void>(`rootfolders/${id}`); }
  browse(path?: string) { return this.get<DirectoryListing>('filesystem', { path }); }

  // Indexers
  indexers() { return this.get<Indexer[]>('indexers'); }
  saveIndexer(i: Indexer) { return i.id ? this.put<Indexer>(`indexers/${i.id}`, i) : this.post<Indexer>('indexers', i); }
  deleteIndexer(id: number) { return this.delete<void>(`indexers/${id}`); }
  testIndexer(i: Indexer) { return this.post<IndexerTestResult>('indexers/test', i); }
  search(query: string, type: SearchType) {
    return this.get<{ results: SearchResult[]; errors: IndexerError[] }>('search', { query, type });
  }

  // Downloads
  downloads() { return this.get<TorrentStatus[]>('downloads'); }
  addMagnet(magnet: string, category?: string) { return this.post<unknown>('downloads/magnet', { magnet, category }); }
  addTorrentFile(file: File, category?: string) {
    const form = new FormData();
    form.append('file', file);
    if (category) form.append('category', category);
    return firstValueFrom(this.http.post(`${API}/downloads/torrent`, form));
  }
  grabRelease(release: ReleaseInfo, category?: string) { return this.post<unknown>('downloads/release', { release, category }); }
  pauseDownload(hash: string) { return this.post<void>(`downloads/${hash}/pause`); }
  resumeDownload(hash: string) { return this.post<void>(`downloads/${hash}/resume`); }
  removeDownload(hash: string, deleteData: boolean) { return this.delete<void>(`downloads/${hash}`, { deleteData }); }

  // Movies
  movies() { return this.get<Movie[]>('movies'); }
  movie(id: number) { return this.get<Movie>(`movies/${id}`); }
  lookupMovies(term: string) { return this.get<MovieLookup[]>('movies/lookup', { term }); }
  addMovie(req: { tmdbId: number; qualityProfileId: number; rootFolderPath: string; monitored: boolean; searchForMovie: boolean }) {
    return this.post<Movie>('movies', req);
  }
  updateMovie(id: number, monitored: boolean, qualityProfileId: number) { return this.put<void>(`movies/${id}`, { monitored, qualityProfileId }); }
  deleteMovie(id: number, deleteFiles: boolean) { return this.delete<void>(`movies/${id}`, { deleteFiles }); }
  searchMovie(id: number) { return this.post<CommandInfo>(`movies/${id}/search`); }
  movieReleases(id: number) { return this.get<{ releases: ReleaseDecision[]; errors: IndexerError[] }>(`movies/${id}/releases`); }
  grabMovieRelease(id: number, release: ReleaseInfo) { return this.post<unknown>(`movies/${id}/releases/grab`, { release }); }
  history(movieId?: number, page = 1, pageSize = 50) {
    return this.get<{ total: number; items: HistoryItem[] }>('history', { movieId, page, pageSize });
  }

  // Series (scaffold)
  lookupSeries(term: string) { return this.get<SeriesLookup[]>('series/lookup', { term }); }
}
