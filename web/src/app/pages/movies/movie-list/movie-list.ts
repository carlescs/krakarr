import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, DestroyRef, ElementRef, Injector, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { Library, MovieQuery } from '../../../core/library';
import { MovieFilter, MovieGrouping, MovieListItem, MovieSortKey } from '../../../core/models';
import { SEARCH_DEBOUNCE_MS, VirtualRowDataSource } from '../../../core/paged-list';
import { PagedSelection, ViewMode, storeView, storedView } from '../../../core/selection';
import { Subscriptions } from '../../../core/subscriptions';
import { TableSort } from '../../../core/table-sort';
import { Toasts } from '../../../core/toast';
import { gridMetrics, gridSlots, observeContainerWidth, KrVirtualTable } from '../../../core/virtual-scroll';
import { BulkActions } from '../../../shared/bulk-actions/bulk-actions';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { Poster } from '../../../shared/poster/poster';
import { Progress } from '../../../shared/progress/progress';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { ViewToggle } from '../../../shared/view-toggle/view-toggle';
import { CollectionGrid } from '../collection-grid/collection-grid';

const GROUPING_KEY = 'krakarr.movies.groupBy';
const VIEW_KEY = 'krakarr.movies.view';
const TABLE_ROW_HEIGHT = 44;

function storedGrouping(): MovieGrouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

interface MovieGridCardsRow {
  key: string;
  start: number;
  count: number;
}

interface MovieTableRow {
  key: string;
  index: number;
}

@Component({
  selector: 'kr-movie-list',
  host: { class: 'block' },
  imports: [
    ScrollingModule,
    KrVirtualTable,
    RouterLink,
    FormsModule,
    Poster,
    Progress,
    PosterGridSkeleton,
    MoreMenu,
    BulkActions,
    ViewToggle,
    SortHeader,
    CollectionGrid,
  ],
  templateUrl: './movie-list.html',
  styleUrl: './movie-list.scss',
})
export class MovieListPage {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly cache = inject(Library).movies;
  private readonly subscriptions = inject(Subscriptions);
  private readonly toasts = inject(Toasts);

  protected readonly loading = this.cache.loading;
  protected readonly total = this.cache.total;
  protected readonly libraryTotal = this.cache.libraryTotal;
  protected readonly query = signal('');
  private readonly committedQuery = signal('');
  protected readonly filter = signal<MovieFilter>('all');
  protected readonly grouping = signal<MovieGrouping>(storedGrouping());
  protected readonly view = signal<ViewMode>(storedView(VIEW_KEY));
  /** Grid selection mode: cards toggle selection instead of opening the movie. The table always has checkboxes. */
  protected readonly selecting = signal(false);
  protected readonly following = this.subscriptions.movieIds;
  protected readonly tableRowHeight = TABLE_ROW_HEIGHT;
  protected readonly slots = gridSlots;

  /** Sort state shared with `SortHeader`; actual sorting happens on the server. */
  protected readonly sort = new TableSort<MovieListItem, MovieSortKey>(
    signal<MovieListItem[]>([]),
    {
      title: 'source',
      year: m => m.year || null,
      status: m => this.statusRank(m),
    },
    { key: 'title' },
  );

  private readonly listQuery = computed<MovieQuery>(() => {
    const s = this.sort.state();
    const q = this.committedQuery().trim();
    return { q: q || undefined, filter: this.filter(), sort: s.key ?? 'title', desc: s.desc ? true : undefined, groupBy: 'none' };
  });

  /** Search as sent to the server (debounced), shared with the collection cards. */
  protected readonly searchTerm = computed(() => this.committedQuery().trim());

  private readonly containerWidth = signal(0);
  private readonly viewportWidth = signal(typeof window !== 'undefined' ? window.innerWidth : 1024);
  protected readonly metrics = computed(() => gridMetrics(this.containerWidth(), this.viewportWidth(), 'movie'));
  protected readonly gridTemplate = computed(() => `repeat(${this.metrics().cols}, minmax(0, 1fr))`);

  private readonly flatGridRows = computed<MovieGridCardsRow[]>(() => {
    const total = this.total();
    const cols = this.metrics().cols;
    const count = Math.ceil(total / cols);
    const rows: MovieGridCardsRow[] = new Array(count);
    for (let r = 0; r < count; r++) {
      const start = r * cols;
      rows[r] = { key: `r-${r}`, start, count: Math.min(cols, total - start) };
    }
    return rows;
  });

  private readonly tableRows = computed<MovieTableRow[]>(() => {
    const total = this.total();
    const rows: MovieTableRow[] = new Array(total);
    for (let i = 0; i < total; i++) rows[i] = { key: `m-${i}`, index: i };
    return rows;
  });

  protected readonly flatGridDataSource: VirtualRowDataSource<MovieGridCardsRow>;
  protected readonly tableDataSource: VirtualRowDataSource<MovieTableRow>;

  protected readonly selection = new PagedSelection(
    this.total,
    () => this.cache.loadedIds(),
    () => this.cache.allLoaded(),
    (a, b) => this.cache.idsBetween(a, b),
    () => this.api.movieIds(this.cache.currentQuery()),
    e => this.toasts.error(errorMessage(e)),
    (selected, total) => this.toasts.show(`Selected the first ${selected} of ${total} movies (bulk actions are limited to ${selected}).`, 'info', 7000),
  );

  /** The bulk toolbar shows while something is selected in the table, and throughout grid selection mode. */
  protected readonly showBulk = computed(() =>
    this.grouping() === 'none' && (this.view() === 'table' ? this.selection.count() > 0 : this.selecting()));

  /** Live download progress per movie, while connected; otherwise the list's own snapshot is used. */
  private readonly liveProgress = computed(() => {
    if (!this.events.connected()) return null;
    const byMovie = new Map<number, number>();
    for (const d of this.events.downloads()) {
      if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
    }
    return byMovie;
  });

  private readonly liveDownloadingKey = computed(() => {
    const live = this.liveProgress();
    if (!live) return null;
    return [...live.keys()].sort((a, b) => a - b).join(',');
  });

  constructor() {
    const injector = inject(Injector);
    const destroyRef = inject(DestroyRef);
    const host = inject(ElementRef<HTMLElement>);
    const debounceMs = inject(SEARCH_DEBOUNCE_MS);

    this.flatGridDataSource = new VirtualRowDataSource(
      this.flatGridRows,
      (rows, start, end) => [rows[start].start, rows[end].start + rows[end].count - 1],
      (s, e) => this.cache.ensureRange(s, e),
      injector,
    );
    this.tableDataSource = new VirtualRowDataSource(
      this.tableRows,
      (rows, start, end) => [rows[start].index, rows[end].index],
      (s, e) => this.cache.ensureRange(s, e),
      injector,
    );

    void this.subscriptions.refresh();
    this.cache.setQuery(untracked(() => this.listQuery()));
    destroyRef.onDestroy(this.cache.attach());
    destroyRef.onDestroy(
      observeContainerWidth(host.nativeElement, (cw, vw) => {
        this.containerWidth.set(cw);
        this.viewportWidth.set(vw);
      }),
    );
    destroyRef.onDestroy(() => {
      this.flatGridDataSource.destroy();
      this.tableDataSource.destroy();
    });

    effect(onCleanup => {
      const q = this.query();
      if (debounceMs <= 0 || q.trim() === '') {
        this.committedQuery.set(q);
        return;
      }
      const timer = setTimeout(() => this.committedQuery.set(q), debounceMs);
      onCleanup(() => clearTimeout(timer));
    });

    effect(() => {
      const next = this.listQuery();
      const changed = this.cache.setQuery(next);
      if (changed && typeof window !== 'undefined' && window.scrollY > 0) {
        window.scrollTo({ top: 0 });
      }
    });

    // Narrow the selection when the query changes. Plain reloads of the same query keep it (and the Shift+click anchor).
    let lastRevision = this.cache.queryRevision();
    effect(() => {
      const rev = this.cache.queryRevision();
      if (rev !== lastRevision) {
        lastRevision = rev;
        void untracked(() => this.selection.reconcile());
      }
    });

    let lastDownloadingKey = this.liveDownloadingKey();
    effect(() => {
      const key = this.liveDownloadingKey();
      if (key !== lastDownloadingKey) {
        lastDownloadingKey = key;
        // What is downloading decides membership of the Downloading filter and the order of the status sort.
        untracked(() => {
          if (this.filter() === 'downloading') {
            void this.cache.reload().then(() => this.selection.reconcile());
          } else if (this.sort.state().key === 'status') {
            void this.cache.reload();
          }
        });
      }
    });
  }

  protected movieAt(index: number): MovieListItem | undefined {
    return this.cache.at(index);
  }

  protected isRowSelected(row: MovieTableRow): boolean {
    const m = this.cache.at(row.index);
    return m != null && this.selection.has(m.id);
  }

  protected trackRow(_index: number, row: { key: string }): string {
    return row.key;
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
    void this.selection.toggle(m.id, (event as MouseEvent | KeyboardEvent).shiftKey);
  }

  /** Switches between movie cards and collection cards. Collections have no selection mode. */
  protected setGrouping(value: MovieGrouping) {
    this.grouping.set(value);
    if (value === 'collection') this.stopSelecting();
    try { localStorage.setItem(GROUPING_KEY, value); } catch { /* storage unavailable */ }
  }
}
