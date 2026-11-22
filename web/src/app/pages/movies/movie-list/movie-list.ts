import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Events } from '../../../core/events';
import { Auth } from '../../../core/auth';
import { Library } from '../../../core/library';
import { Subscriptions } from '../../../core/subscriptions';
import { MovieListItem } from '../../../core/models';
import { MovieGroup, groupByCollection } from '../../../core/movie-groups';
import { NearViewport } from '../../../shared/near-viewport/near-viewport';
import { Poster } from '../../../shared/poster/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { Progress } from '../../../shared/progress/progress';
import { BulkActions } from '../../../shared/bulk-actions/bulk-actions';
import { ViewToggle } from '../../../shared/view-toggle/view-toggle';
import { Selection, ViewMode, storeView, storedView } from '../../../core/selection';
import { TableSort } from '../../../core/table-sort';
import { SortHeader } from '../../../shared/sort-header/sort-header';

type Filter = 'all' | 'following' | 'missing' | 'downloading' | 'downloaded' | 'unmonitored';
type Grouping = 'none' | 'collection';

const GROUPING_KEY = 'krakarr.movies.groupBy';
const VIEW_KEY = 'krakarr.movies.view';

function storedGrouping(): Grouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, NgTemplateOutlet, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu, BulkActions, ViewToggle, SortHeader],
  templateUrl: './movie-list.html',
  styleUrl: './movie-list.scss',
})
export class MovieListPage {
  protected readonly auth = inject(Auth);
  private readonly events = inject(Events);
  private readonly library = inject(Library);
  private readonly subscriptions = inject(Subscriptions);

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.library.movies.items() === null);
  protected readonly movies = computed(() => this.library.movies.items() ?? []);
  private readonly collections = computed(() => this.library.movieCollections.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly grouping = signal<Grouping>(storedGrouping());
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());
  protected readonly view = signal<ViewMode>(storedView(VIEW_KEY));
  /** Grid selection mode: cards toggle selection instead of opening the movie. The table always has checkboxes. */
  protected readonly selecting = signal(false);
  protected readonly following = this.subscriptions.movieIds;

  private readonly collectionNames = computed(() => new Map(this.collections().map(c => [c.id, c.name.toLowerCase()])));

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    const names = this.collectionNames();
    const following = filter === 'following' ? this.following() : null;
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q) && !(m.collectionId != null && names.get(m.collectionId)?.includes(q))) return false;
      switch (filter) {
        case 'following': return following!.has(m.id);
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloading': return this.progressOf(m) != null;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

  /** The filtered list in the chosen sort order (the API sorts by title). Groups keep their own order. */
  protected readonly sort = new TableSort(this.filtered, {
    title: 'source',
    year: m => m.year || null,
    status: m => this.statusRank(m),
  }, { key: 'title' });
  private readonly sorted = this.sort.rows;

  /** Cards rendered so far: more are added as the end of the list scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter(), this.grouping(), this.sort.state()], computation: () => GRID_CHUNK });

  /** Flat list: the first `limit` movies. */
  protected readonly visible = computed(() => this.sorted().slice(0, this.limit()));

  /** Sections when grouping by collection; null for the flat list. */
  private readonly groups = computed(() => this.grouping() === 'collection' ? groupByCollection(this.filtered(), this.collections()) : null);

  /** Every filtered movie in display order: what "select all" and Shift+click ranges work on. */
  private readonly ordered = computed(() => this.groups()?.flatMap(g => g.movies) ?? this.sorted());
  protected readonly selection = new Selection(this.ordered);

  /** The bulk toolbar shows while something is selected in the table, and throughout grid selection mode. */
  protected readonly showBulk = computed(() => this.view() === 'table' ? this.selection.count() > 0 : this.selecting());

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
    if (!visible) return this.visible().length < this.sorted().length;
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
    void this.subscriptions.refresh();
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(this.library.movies.attach());
    destroyRef.onDestroy(this.library.movieCollections.attach());
  }

  protected progressOf(m: MovieListItem): number | null {
    const live = this.liveProgress();
    return live ? (live.get(m.id) ?? null) : (m.downloadProgress ?? null);
  }

  /** Status order for sorting: missing, unmonitored, downloading, downloaded. */
  protected statusRank(m: MovieListItem): number {
    return m.hasFile ? 3 : this.progressOf(m) != null ? 2 : !m.monitored ? 1 : 0;
  }

  protected statusLabel(m: MovieListItem): string {
    const progress = this.progressOf(m);
    return m.hasFile ? 'Downloaded' : progress != null ? `Downloading ${Math.round(progress)}%` : !m.monitored ? 'Unmonitored' : 'Missing';
  }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }

  protected setView(mode: ViewMode) {
    this.view.set(mode);
    storeView(VIEW_KEY, mode);
    // Keep a selection made in the table visible (and actionable) in the grid.
    this.selecting.set(mode === 'grid' && this.selection.count() > 0);
  }

  protected stopSelecting() {
    this.selecting.set(false);
    this.selection.clear();
  }

  /** Grid card click (or Enter/Space): toggles the movie in selection mode (Shift selects a range), otherwise follows the link. */
  protected cardClick(event: Event, m: MovieListItem) {
    if (!this.selecting()) return;
    event.preventDefault();
    this.selection.toggle(m.id, (event as MouseEvent | KeyboardEvent).shiftKey);
  }

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
