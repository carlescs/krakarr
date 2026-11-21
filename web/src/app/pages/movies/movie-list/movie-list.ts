import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Events } from '../../../core/events';
import { Auth } from '../../../core/auth';
import { Library } from '../../../core/library';
import { MovieListItem } from '../../../core/models';
import { MovieGroup, groupByCollection } from '../../../core/movie-groups';
import { NearViewport } from '../../../shared/near-viewport/near-viewport';
import { Poster } from '../../../shared/poster/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { Progress } from '../../../shared/progress/progress';

type Filter = 'all' | 'missing' | 'downloading' | 'downloaded' | 'unmonitored';
type Grouping = 'none' | 'collection';

const GROUPING_KEY = 'krakarr.movies.groupBy';

function storedGrouping(): Grouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, NgTemplateOutlet, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu],
  templateUrl: './movie-list.html',
  styleUrl: './movie-list.scss',
})
export class MovieListPage {
  protected readonly auth = inject(Auth);
  private readonly events = inject(Events);
  private readonly library = inject(Library);

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.library.movies.items() === null);
  protected readonly movies = computed(() => this.library.movies.items() ?? []);
  private readonly collections = computed(() => this.library.movieCollections.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly grouping = signal<Grouping>(storedGrouping());
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());

  private readonly collectionNames = computed(() => new Map(this.collections().map(c => [c.id, c.name.toLowerCase()])));

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    const names = this.collectionNames();
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q) && !(m.collectionId != null && names.get(m.collectionId)?.includes(q))) return false;
      switch (filter) {
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloading': return this.progressOf(m) != null;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

  /** Cards rendered so far: more are added as the end of the list scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter(), this.grouping()], computation: () => GRID_CHUNK });

  /** Flat grid: the first `limit` movies. */
  protected readonly visible = computed(() => this.filtered().slice(0, this.limit()));

  /** Sections when grouping by collection; null for the flat grid. */
  private readonly groups = computed(() => this.grouping() === 'collection' ? groupByCollection(this.filtered(), this.collections()) : null);

  /** The sections with the first `limit` cards spread over the expanded ones; null for the flat grid. */
  protected readonly visibleGroups = computed(() => {
    const groups = this.groups();
    if (!groups) return null;
    const collapsed = this.collapsed();
    let left = this.limit();
    const result: { group: MovieGroup; movies: MovieListItem[] }[] = [];
    for (const group of groups) {
      if (left <= 0) break;
      const movies = collapsed.has(group.key) ? [] : group.movies.slice(0, left);
      left -= movies.length;
      result.push({ group, movies });
    }
    return result;
  });

  protected readonly hasMore = computed(() => {
    const visible = this.visibleGroups();
    if (!visible) return this.visible().length < this.filtered().length;
    const collapsed = this.collapsed();
    return visible.length < this.groups()!.length
      || visible.some(g => !collapsed.has(g.group.key) && g.movies.length < g.group.movies.length);
  });

  /** Live download progress per movie, while connected; otherwise the list's own snapshot is used. */
  private readonly liveProgress = computed(() => {
    if (!this.events.connected()) return null;
    const byMovie = new Map<number, number>();
    for (const d of this.events.downloads()) if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
    return byMovie;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(this.library.movies.attach());
    destroyRef.onDestroy(this.library.movieCollections.attach());
  }

  protected progressOf(m: MovieListItem): number | null {
    const live = this.liveProgress();
    return live ? (live.get(m.id) ?? null) : (m.downloadProgress ?? null);
  }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }

  protected setGrouping(value: Grouping) {
    this.grouping.set(value);
    try { localStorage.setItem(GROUPING_KEY, value); } catch { /* storage unavailable */ }
  }

  protected toggle(key: string) {
    this.collapsed.update(set => {
      const next = new Set(set);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }
}
