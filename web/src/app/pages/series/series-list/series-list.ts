import { ScrollingModule } from '@angular/cdk/scrolling';
import { DatePipe } from '@angular/common';
import { Component, DestroyRef, ElementRef, Injector, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Library, SeriesQuery } from '../../../core/library';
import { SeriesFilter, SeriesListItem, SeriesSortKey } from '../../../core/models';
import { SEARCH_DEBOUNCE_MS, VirtualRowDataSource } from '../../../core/paged-list';
import { PagedSelection, ViewMode, storeView, storedView } from '../../../core/selection';
import { Subscriptions } from '../../../core/subscriptions';
import { TableSort } from '../../../core/table-sort';
import { Toasts } from '../../../core/toast';
import { gridMetrics, observeContainerWidth } from '../../../core/virtual-scroll';
import { BulkActions } from '../../../shared/bulk-actions/bulk-actions';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { Poster } from '../../../shared/poster/poster';
import { Progress } from '../../../shared/progress/progress';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { ViewToggle } from '../../../shared/view-toggle/view-toggle';

const VIEW_KEY = 'krakarr.series.view';
const TABLE_ROW_HEIGHT = 44;

interface SeriesGridRow {
  key: number;
  start: number;
  count: number;
  indices: number[];
}

@Component({
  selector: 'kr-series-list',
  host: { class: 'block' },
  imports: [
    ScrollingModule,
    RouterLink,
    FormsModule,
    DatePipe,
    Poster,
    Progress,
    PosterGridSkeleton,
    MoreMenu,
    BulkActions,
    ViewToggle,
    SortHeader,
  ],
  templateUrl: './series-list.html',
  styleUrl: './series-list.scss',
})
export class SeriesListPage {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly cache = inject(Library).series;
  private readonly subscriptions = inject(Subscriptions);
  private readonly toasts = inject(Toasts);

  protected readonly loading = this.cache.loading;
  protected readonly total = this.cache.total;
  protected readonly libraryTotal = this.cache.libraryTotal;
  protected readonly query = signal('');
  private readonly committedQuery = signal('');
  protected readonly filter = signal<SeriesFilter>('all');
  protected readonly view = signal<ViewMode>(storedView(VIEW_KEY));
  /** Grid selection mode: cards toggle selection instead of opening the series. The table always has checkboxes. */
  protected readonly selecting = signal(false);
  protected readonly following = this.subscriptions.seriesIds;
  protected readonly tableRowHeight = TABLE_ROW_HEIGHT;

  /** Sort state shared with `SortHeader`; actual sorting happens on the server. */
  protected readonly sort = new TableSort<SeriesListItem, SeriesSortKey>(
    signal<SeriesListItem[]>([]),
    {
      title: 'source',
      year: s => s.year || null,
      status: s => s.status === 'Ended',
      episodes: s => this.percent(s),
      nextAiring: s => (s.nextAiring ? Date.parse(s.nextAiring) : null),
    },
    { key: 'title' },
  );

  private readonly listQuery = computed<SeriesQuery>(() => {
    const s = this.sort.state();
    const q = this.committedQuery().trim();
    return {
      q: q || undefined,
      filter: this.filter(),
      sort: s.key ?? 'title',
      desc: s.desc || undefined,
    };
  });

  private readonly containerWidth = signal(0);
  private readonly viewportWidth = signal(typeof window !== 'undefined' ? window.innerWidth : 1024);
  protected readonly metrics = computed(() => gridMetrics(this.containerWidth(), this.viewportWidth(), 'series'));
  protected readonly gridTemplate = computed(() => `repeat(${this.metrics().cols}, minmax(0, 1fr))`);

  private readonly gridRows = computed<SeriesGridRow[]>(() => {
    const total = this.total();
    const cols = this.metrics().cols;
    const count = Math.ceil(total / cols);
    const rows: SeriesGridRow[] = new Array(count);
    for (let r = 0; r < count; r++) {
      const start = r * cols;
      const len = Math.min(cols, total - start);
      const indices: number[] = new Array(len);
      for (let i = 0; i < len; i++) indices[i] = start + i;
      rows[r] = { key: r, start, count: len, indices };
    }
    return rows;
  });

  private readonly tableRows = computed<number[]>(() => {
    const total = this.total();
    const rows: number[] = new Array(total);
    for (let i = 0; i < total; i++) rows[i] = i;
    return rows;
  });

  protected readonly gridDataSource: VirtualRowDataSource<SeriesGridRow>;
  protected readonly tableDataSource: VirtualRowDataSource<number>;

  protected readonly selection = new PagedSelection(
    this.total,
    () => this.cache.loadedIds(),
    () => this.cache.allLoaded(),
    (a, b) => this.cache.idsBetween(a, b),
    () => this.api.seriesIds(this.cache.currentQuery()),
    e => this.toasts.error(errorMessage(e)),
  );

  /** The bulk toolbar shows while something is selected in the table, and throughout grid selection mode. */
  protected readonly showBulk = computed(() => (this.view() === 'table' ? this.selection.count() > 0 : this.selecting()));

  constructor() {
    const injector = inject(Injector);
    const destroyRef = inject(DestroyRef);
    const host = inject(ElementRef<HTMLElement>);
    const debounceMs = inject(SEARCH_DEBOUNCE_MS);

    this.gridDataSource = new VirtualRowDataSource(
      this.gridRows,
      (rows, start, end) => [rows[start].start, rows[end].start + rows[end].count - 1],
      (s, e) => this.cache.ensureRange(s, e),
      injector,
    );
    this.tableDataSource = new VirtualRowDataSource(
      this.tableRows,
      (_rows, start, end) => [start, end],
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
      this.gridDataSource.destroy();
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

    let lastRevision = this.cache.revision();
    effect(() => {
      const rev = this.cache.revision();
      if (rev !== lastRevision) {
        lastRevision = rev;
        void untracked(() => this.selection.reconcile());
      }
    });
  }

  protected seriesAt(index: number): SeriesListItem | undefined {
    return this.cache.at(index);
  }

  protected isRowSelected(index: number): boolean {
    const s = this.cache.at(index);
    return s != null && this.selection.has(s.id);
  }

  protected trackGridRow(_index: number, row: SeriesGridRow): number {
    return row.key;
  }

  protected trackIndex(_index: number, item: number): number {
    return item;
  }

  protected percent(s: SeriesListItem) {
    return s.episodeCount === 0 ? 0 : (s.episodeFileCount / s.episodeCount) * 100;
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

  /** Grid card click (or Enter/Space): toggles the series in selection mode (Shift selects a range), otherwise follows the link. */
  protected cardClick(event: Event, s: SeriesListItem) {
    if (!this.selecting()) return;
    event.preventDefault();
    void this.selection.toggle(s.id, (event as MouseEvent | KeyboardEvent).shiftKey);
  }

  protected progressColor(s: SeriesListItem) {
    return !s.monitored ? 'bg-slate-500' : s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500' : 'bg-rose-500';
  }
}
