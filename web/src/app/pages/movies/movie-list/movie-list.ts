import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, DestroyRef, ElementRef, Injector, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { Library, MovieQuery } from '../../../core/library';
import { MovieCollectionGroup, MovieFilter, MovieGrouping, MovieListItem, MovieSortKey } from '../../../core/models';
import { SEARCH_DEBOUNCE_MS, VirtualRowDataSource } from '../../../core/paged-list';
import { PagedSelection, ViewMode, storeView, storedView } from '../../../core/selection';
import { Subscriptions } from '../../../core/subscriptions';
import { TableSort } from '../../../core/table-sort';
import { Toasts } from '../../../core/toast';
import { KrRowHeights, gridMetrics, gridSlots, observeContainerWidth } from '../../../core/virtual-scroll';
import { BulkActions } from '../../../shared/bulk-actions/bulk-actions';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { Poster } from '../../../shared/poster/poster';
import { Progress } from '../../../shared/progress/progress';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { ViewToggle } from '../../../shared/view-toggle/view-toggle';

const GROUPING_KEY = 'krakarr.movies.groupBy';
const VIEW_KEY = 'krakarr.movies.view';
const TABLE_ROW_HEIGHT = 44;
const FIRST_HEADER_HEIGHT = 48;
const NEXT_HEADER_HEIGHT = 64;

function storedGrouping(): MovieGrouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

interface MovieGridCardsRow {
  kind: 'cards';
  key: string;
  start: number;
  count: number;
}

interface MovieGridHeaderRow {
  kind: 'header';
  key: string;
  group: MovieCollectionGroup;
  first: boolean;
  height: number;
}

type MovieGroupedGridRow = MovieGridHeaderRow | MovieGridCardsRow;

type MovieTableRow =
  | { kind: 'header'; key: string; group: MovieCollectionGroup }
  | { kind: 'movie'; key: string; index: number };

function cardRowRange(rows: readonly MovieGroupedGridRow[], start: number, end: number): [number, number] | null {
  let min = Infinity;
  let max = -1;
  for (let i = start; i <= end; i++) {
    const row = rows[i];
    if (row.kind === 'cards') {
      if (row.start < min) min = row.start;
      const last = row.start + row.count - 1;
      if (last > max) max = last;
    }
  }
  return max >= min ? [min, max] : null;
}

function tableRowRange(rows: readonly MovieTableRow[], start: number, end: number): [number, number] | null {
  let min = Infinity;
  let max = -1;
  for (let i = start; i <= end; i++) {
    const row = rows[i];
    if (row.kind === 'movie') {
      if (row.index < min) min = row.index;
      if (row.index > max) max = row.index;
    }
  }
  return max >= min ? [min, max] : null;
}

@Component({
  selector: 'kr-movie-list',
  host: { class: 'block' },
  imports: [
    ScrollingModule,
    RouterLink,
    FormsModule,
    Poster,
    Progress,
    PosterGridSkeleton,
    MoreMenu,
    BulkActions,
    ViewToggle,
    SortHeader,
    KrRowHeights,
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
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());
  protected readonly view = signal<ViewMode>(storedView(VIEW_KEY));
  /** Grid selection mode: cards toggle selection instead of opening the movie. The table always has checkboxes. */
  protected readonly selecting = signal(false);
  protected readonly following = this.subscriptions.movieIds;
  protected readonly tableRowHeight = TABLE_ROW_HEIGHT;
  protected readonly slots = gridSlots;

  /** Sort state shared with `SortHeader`; actual sorting happens on the server. Groups keep their own order. */
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
    const groupBy = this.grouping();
    return {
      q: q || undefined,
      filter: this.filter(),
      sort: groupBy === 'none' ? (s.key ?? 'title') : undefined,
      desc: groupBy === 'none' && s.desc ? true : undefined,
      groupBy,
    };
  });

  /** Collection groups returned by the server when `grouping === 'collection'`; null for flat views. */
  protected readonly groups = computed<MovieCollectionGroup[] | null>(() => {
    if (this.grouping() !== 'collection') return null;
    return this.cache.meta()?.groups ?? null;
  });

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
      rows[r] = { kind: 'cards', key: `r-${r}`, start, count: Math.min(cols, total - start) };
    }
    return rows;
  });

  private readonly groupedGridRows = computed<MovieGroupedGridRow[]>(() => {
    const groups = this.groups();
    if (!groups) return [];
    const cols = this.metrics().cols;
    const collapsed = this.collapsed();
    const rows: MovieGroupedGridRow[] = [];
    let offset = 0;
    for (let gi = 0; gi < groups.length; gi++) {
      const group = groups[gi];
      const first = gi === 0;
      rows.push({
        kind: 'header',
        key: `h-${group.key}`,
        group,
        first,
        height: first ? FIRST_HEADER_HEIGHT : NEXT_HEADER_HEIGHT,
      });
      if (!collapsed.has(group.key)) {
        const rowCount = Math.ceil(group.count / cols);
        for (let r = 0; r < rowCount; r++) {
          const start = offset + r * cols;
          rows.push({ kind: 'cards', key: `c-${group.key}-${r}`, start, count: Math.min(cols, offset + group.count - start) });
        }
      }
      offset += group.count;
    }
    return rows;
  });

  protected readonly groupedGridHeights = computed<number[]>(() => {
    const cardHeight = this.metrics().rowHeight;
    return this.groupedGridRows().map(row => (row.kind === 'header' ? row.height : cardHeight));
  });

  private readonly tableRows = computed<MovieTableRow[]>(() => {
    const groups = this.groups();
    if (!groups) {
      const total = this.total();
      const rows: MovieTableRow[] = new Array(total);
      for (let i = 0; i < total; i++) rows[i] = { kind: 'movie', key: `m-${i}`, index: i };
      return rows;
    }
    const collapsed = this.collapsed();
    const rows: MovieTableRow[] = [];
    let offset = 0;
    for (const group of groups) {
      rows.push({ kind: 'header', key: `h-${group.key}`, group });
      if (!collapsed.has(group.key)) {
        for (let i = 0; i < group.count; i++) {
          rows.push({ kind: 'movie', key: `m-${offset + i}`, index: offset + i });
        }
      }
      offset += group.count;
    }
    return rows;
  });

  protected readonly flatGridDataSource: VirtualRowDataSource<MovieGridCardsRow>;
  protected readonly groupedGridDataSource: VirtualRowDataSource<MovieGroupedGridRow>;
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
  protected readonly showBulk = computed(() => (this.view() === 'table' ? this.selection.count() > 0 : this.selecting()));

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
    this.groupedGridDataSource = new VirtualRowDataSource(
      this.groupedGridRows,
      cardRowRange,
      (s, e) => this.cache.ensureRange(s, e),
      injector,
    );
    this.tableDataSource = new VirtualRowDataSource(
      this.tableRows,
      tableRowRange,
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
      this.groupedGridDataSource.destroy();
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
          } else if (this.grouping() === 'none' && this.sort.state().key === 'status') {
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
    if (row.kind !== 'movie') return false;
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

  protected setGrouping(value: MovieGrouping) {
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
