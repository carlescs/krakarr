import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Events } from '../../../core/events';
import { DiscoverItem, MovieCollectionDetails, MovieListItem, MovieLookup } from '../../../core/models';
import { DiscoverAdd } from '../../../shared/discover-add/discover-add';
import { MediaCard } from '../../../shared/media-card/media-card';
import { POSTER_GRID } from '../../../shared/poster-grid/poster-grid';
import { Poster } from '../../../shared/poster/poster';
import { Progress } from '../../../shared/progress/progress';

const SHOW_MISSING_KEY = 'krakarr.collections.showMissing';

function storedShowMissing(): boolean {
  try { return localStorage.getItem(SHOW_MISSING_KEY) === 'true'; } catch { return false; }
}

/** One card of the collection grid: a library movie, or (with "Show missing") a collection movie not in the library. */
export type CollectionEntry =
  | { kind: 'library'; key: string; year: number; title: string; movie: MovieListItem }
  | { kind: 'missing'; key: string; year: number; title: string; item: DiscoverItem };

/** Library and missing movies in one list, in release order; titles without a known year go last. */
export function collectionEntries(movies: readonly MovieListItem[], missing: readonly MovieLookup[]): CollectionEntry[] {
  const entries: CollectionEntry[] = [
    ...movies.map(m => ({ kind: 'library' as const, key: `m${m.id}`, year: m.year, title: m.title, movie: m })),
    ...missing.map(l => ({ kind: 'missing' as const, key: `t${l.tmdbId}`, year: l.year, title: l.title, item: { kind: 'movie' as const, item: l } })),
  ];
  return entries.sort((a, b) => (a.year || 9999) - (b.year || 9999) || a.title.localeCompare(b.title));
}

/** A TMDB collection: its movies in the library and, on request, the ones still missing from it. */
@Component({
  selector: 'kr-collection-detail',
  imports: [RouterLink, Poster, Progress, MediaCard, DiscoverAdd],
  templateUrl: './collection-detail.html',
  styleUrl: './collection-detail.scss',
})
export class CollectionDetailPage {
  private readonly api = inject(Api);
  private readonly events = inject(Events);

  /** Route parameter. */
  readonly id = input.required<string>();

  protected readonly grid = POSTER_GRID;
  protected readonly collection = signal<MovieCollectionDetails | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly showMissing = signal(storedShowMissing());
  /** Collection movies outside the library; null until fetched from TMDB. */
  protected readonly missing = signal<MovieLookup[] | null>(null);
  protected readonly missingLoading = signal(false);
  protected readonly missingError = signal<string | null>(null);
  protected readonly adding = signal<DiscoverItem | null>(null);

  protected readonly downloaded = computed(() => this.collection()?.movies.filter(m => m.hasFile).length ?? 0);
  protected readonly entries = computed(() =>
    collectionEntries(this.collection()?.movies ?? [], this.showMissing() ? (this.missing() ?? []) : []));

  /** Live download progress per movie, while connected; otherwise the snapshot from the API is used. */
  private readonly liveProgress = computed(() => {
    if (!this.events.connected()) return null;
    const byMovie = new Map<number, number>();
    for (const d of this.events.downloads()) {
      if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
    }
    return byMovie;
  });

  constructor() {
    effect(() => {
      const id = Number(this.id());
      untracked(() => {
        this.collection.set(null);
        this.missing.set(null);
        this.error.set(null);
        void this.load(id);
        if (this.showMissing()) void this.loadMissing(id);
      });
    });
  }

  private async load(id: number) {
    try {
      const c = await this.api.movieCollection(id);
      if (id === Number(this.id())) this.collection.set(c);
    } catch (e) {
      if (id === Number(this.id())) this.error.set(errorMessage(e));
    }
  }

  private async loadMissing(id: number) {
    this.missingLoading.set(true);
    this.missingError.set(null);
    try {
      const list = await this.api.movieCollectionMissing(id);
      if (id === Number(this.id())) this.missing.set(list);
    } catch (e) {
      if (id === Number(this.id())) this.missingError.set(errorMessage(e));
    } finally {
      this.missingLoading.set(false);
    }
  }

  protected toggleMissing() {
    const next = !this.showMissing();
    this.showMissing.set(next);
    try { localStorage.setItem(SHOW_MISSING_KEY, String(next)); } catch { /* storage unavailable */ }
    if (next && this.missing() == null && !this.missingLoading()) void this.loadMissing(Number(this.id()));
  }

  /** A missing movie was added: it now belongs to the library, so reload both lists. */
  protected added() {
    this.adding.set(null);
    const id = Number(this.id());
    void this.load(id);
    void this.loadMissing(id);
  }

  protected progressOf(m: MovieListItem): number | null {
    const live = this.liveProgress();
    return live ? (live.get(m.id) ?? null) : (m.downloadProgress ?? null);
  }
}
