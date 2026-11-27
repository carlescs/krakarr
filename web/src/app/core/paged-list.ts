import { CollectionViewer, DataSource } from '@angular/cdk/collections';
import { InjectionToken, Injector, Signal, effect, signal } from '@angular/core';
import { BehaviorSubject, Observable, Subscription } from 'rxjs';

/** Default page size for paginated library endpoints (divisible by 3, 4, 5 and 6 poster columns). */
export const LIBRARY_PAGE_SIZE = 60;

/** Bounded number of pages kept in memory per list so scrolling huge libraries stays light on RAM. */
export const MAX_CACHED_PAGES = 20;

/** Debounce delay before firing a server-side search query when typing in a library filter box. */
export const SEARCH_DEBOUNCE_MS = new InjectionToken<number>('SEARCH_DEBOUNCE_MS', {
  providedIn: 'root',
  factory: () => 200,
});

/** Shape shared by paginated library responses (`GET /movies`, `GET /series`). */
export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  libraryTotal: number;
}

function stableKey(query: object): string {
  const entries = Object.entries(query)
    .filter(([, v]) => v !== undefined && v !== null && v !== '' && v !== false)
    .sort(([a], [b]) => a.localeCompare(b));
  return JSON.stringify(entries);
}

/**
 * Paginated, memory-bounded cache for a library list that outlives the page showing it.
 *
 * Returning to the page with the same query shows the cached pages immediately and refreshes the visible range in the
 * background; bursts of update events collapse into one reload per `delayMs`. Changing the query keeps the previous
 * results on screen until page 1 of the new query arrives, avoiding flicker.
 */
export class PagedListCache<T extends { id: number }, Q extends object, P extends PagedResponse<T> = PagedResponse<T>> {
  /** True only before the very first load finishes (controls the initial full-page skeleton). */
  readonly loading = signal(true);
  /** Total items matching the current query/filter. */
  readonly total = signal(0);
  /** Total items in the library regardless of query/filter. */
  readonly libraryTotal = signal(0);
  /** Latest page response metadata (e.g. collection groups on movies), or null before the first load. */
  readonly meta = signal<P | null>(null);
  /** Increments whenever cached page contents change, so virtual scroll views reading `at(index)` re-render. */
  readonly version = signal(0);
  /** Increments when a query commit or full reload completes (not when scrolling loads additional pages). */
  readonly revision = signal(0);
  /** Increments only when results for a different query are committed (not on reloads of the same query). */
  readonly queryRevision = signal(0);

  private query: Q;
  private queryKey: string;
  private committedKey: string | null = null;
  private pages = new Map<number, T[]>();
  private inFlight = new Set<number>();
  private activeRange: [number, number] = [1, 1];
  /** Last item range a view asked for, kept so it can be requested again after a commit replaces the pages. */
  private requestedRange: [number, number] | null = null;
  /** Pages loaded by scrolling while a reload was in flight: fresher than the reload's snapshot, so the reload keeps them. */
  private loadedDuringReload = new Set<number>();
  private generation = 0;
  private watchers = 0;
  private reloading?: Promise<void>;
  private queued = false;
  private timer?: ReturnType<typeof setTimeout>;

  constructor(
    initialQuery: Q,
    private readonly fetchPage: (query: Q, page: number, pageSize: number) => Promise<P>,
    private readonly onError: (e: unknown) => void,
    readonly pageSize = LIBRARY_PAGE_SIZE,
    private readonly delayMs = 1000,
    private readonly maxCachedPages = MAX_CACHED_PAGES,
  ) {
    this.query = initialQuery;
    this.queryKey = stableKey(initialQuery);
  }

  /** Current query parameters (without page/pageSize). */
  currentQuery(): Q {
    return this.query;
  }

  /** Updates the active query/filter/sort. Does nothing when unchanged; fetches page 1 when a page is attached. */
  setQuery(next: Q): boolean {
    const key = stableKey(next);
    if (key === this.queryKey) return false;
    this.query = next;
    this.queryKey = key;
    this.activeRange = [1, 1];
    if (this.watchers > 0) {
      void this.loadForNewQuery();
    } else {
      // Dropped cache will be fetched when a page next attaches. Responses still in flight belong to the old query.
      this.generation++;
      this.committedKey = null;
      this.pages.clear();
      this.inFlight.clear();
      this.loading.set(true);
    }
    return true;
  }

  /** A page started showing the list: refreshes the visible pages and keeps them fresh until detached. */
  attach(): () => void {
    this.watchers++;
    if (this.committedKey === this.queryKey) {
      void this.reload();
    } else {
      void this.loadForNewQuery();
    }
    let attached = true;
    return () => {
      if (!attached) return;
      attached = false;
      this.watchers--;
    };
  }

  /** Something in the list changed. Reloads once after `delayMs` if a page shows it; otherwise the next attach reloads. */
  invalidate() {
    if (this.watchers === 0 || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.reload();
    }, this.delayMs);
  }

  /** Reloads the currently visible pages in place (e.g. after a bulk action or background event). */
  reload(): Promise<void> {
    if (this.committedKey !== this.queryKey) return this.loadForNewQuery();
    if (this.reloading) {
      this.queued = true;
      return this.reloading;
    }
    const gen = this.generation;
    const query = this.query;
    const key = this.queryKey;
    const pagesToLoad = new Set<number>([1]);
    for (let p = this.activeRange[0]; p <= this.activeRange[1]; p++) pagesToLoad.add(p);
    this.loadedDuringReload.clear();

    this.reloading = (async () => {
      try {
        const ordered = [...pagesToLoad].sort((a, b) => a - b);
        const results = await Promise.all(ordered.map(p => this.fetchPage(query, p, this.pageSize)));
        if (gen !== this.generation) return;
        const nextPages = new Map<number, T[]>();
        for (let i = 0; i < ordered.length; i++) {
          if (results[i].items.length > 0 || ordered[i] === 1) {
            nextPages.set(ordered[i], results[i].items);
          }
        }
        // Keep pages that scrolling loaded meanwhile (they are at least as fresh); drop the other stale ones.
        for (const p of this.loadedDuringReload) {
          const items = this.pages.get(p);
          if (items && !nextPages.has(p)) nextPages.set(p, items);
        }
        const first = results[0];
        this.pages = nextPages;
        this.committedKey = key;
        this.total.set(first.total);
        this.libraryTotal.set(first.libraryTotal);
        this.meta.set(first);
        this.loading.set(false);
        this.version.update(v => v + 1);
        this.revision.update(v => v + 1);
        this.refillRequestedRange();
      } catch (e) {
        if (gen !== this.generation) return;
        this.loading.set(false);
        this.onError(e);
      } finally {
        this.reloading = undefined;
        this.loadedDuringReload.clear();
        if (this.queued) {
          this.queued = false;
          if (this.watchers > 0) void this.reload();
        }
      }
    })();
    return this.reloading;
  }

  /** Drops all cached pages (e.g. on logout). */
  clear() {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.generation++;
    this.committedKey = null;
    this.pages.clear();
    this.inFlight.clear();
    this.activeRange = [1, 1];
    this.requestedRange = null;
    this.total.set(0);
    this.libraryTotal.set(0);
    this.meta.set(null);
    this.loading.set(true);
    this.version.update(v => v + 1);
  }

  /** Item at 0-based index `index`, or `undefined` if its page has not loaded yet. Reactive via `version()`. */
  at(index: number): T | undefined {
    this.version();
    if (index < 0) return undefined;
    const page = Math.floor(index / this.pageSize) + 1;
    const items = this.pages.get(page);
    return items?.[index % this.pageSize];
  }

  /** All item ids currently cached in index order (when `allLoaded()` is true, this is the complete matching list). */
  loadedIds(): number[] {
    this.version();
    const ids: number[] = [];
    for (const page of [...this.pages.keys()].sort((a, b) => a - b)) {
      const items = this.pages.get(page);
      if (items) for (const item of items) ids.push(item.id);
    }
    return ids;
  }

  /** True when every matching item is present in the cache (so operations like "Select all" need no extra request). */
  allLoaded(): boolean {
    this.version();
    const total = this.total();
    if (total === 0) return true;
    const needed = Math.ceil(total / this.pageSize);
    for (let p = 1; p <= needed; p++) if (!this.pages.has(p)) return false;
    return true;
  }

  /**
   * Returns all item ids between `a` and `b` (inclusive) if both items and every page between them are currently
   * cached; returns null when an unloaded gap lies between them.
   */
  idsBetween(a: number, b: number): number[] | null {
    let idxA = -1;
    let idxB = -1;
    for (const [page, items] of this.pages) {
      const base = (page - 1) * this.pageSize;
      for (let i = 0; i < items.length; i++) {
        if (items[i].id === a) idxA = base + i;
        if (items[i].id === b) idxB = base + i;
      }
    }
    if (idxA < 0 || idxB < 0) return null;
    const from = Math.min(idxA, idxB);
    const to = Math.max(idxA, idxB);
    const firstPage = Math.floor(from / this.pageSize) + 1;
    const lastPage = Math.floor(to / this.pageSize) + 1;
    for (let p = firstPage; p <= lastPage; p++) {
      if (!this.pages.has(p)) return null;
    }
    const result: number[] = [];
    for (let i = from; i <= to; i++) {
      const item = this.pages.get(Math.floor(i / this.pageSize) + 1)?.[i % this.pageSize];
      if (item) result.push(item.id);
    }
    return result;
  }

  /** Ensures every page overlapping 0-based item indices `[startIndex, endIndex]` is loaded or in flight. */
  ensureRange(startIndex: number, endIndex: number) {
    if (endIndex < startIndex) return;
    // Remembered even while a new query loads: views only ask again when their range changes, so the commit re-requests it.
    this.requestedRange = [startIndex, endIndex];
    if (this.committedKey !== this.queryKey) return;
    const total = this.total();
    if (total === 0) return;
    const maxPage = Math.ceil(total / this.pageSize);
    const firstPage = Math.max(1, Math.min(maxPage, Math.floor(Math.max(0, startIndex) / this.pageSize) + 1));
    const lastPage = Math.max(firstPage, Math.min(maxPage, Math.floor(Math.max(0, endIndex) / this.pageSize) + 1));
    this.activeRange = [firstPage, lastPage];

    for (let p = firstPage; p <= lastPage; p++) {
      const existing = this.pages.get(p);
      if (existing) {
        // Refresh LRU position.
        this.pages.delete(p);
        this.pages.set(p, existing);
      } else if (!this.inFlight.has(p)) {
        void this.loadSinglePage(p);
      }
    }
  }

  private async loadForNewQuery(): Promise<void> {
    const gen = ++this.generation;
    const query = this.query;
    const key = this.queryKey;
    this.inFlight.clear();
    this.inFlight.add(1);
    try {
      const res = await this.fetchPage(query, 1, this.pageSize);
      if (gen !== this.generation) return;
      this.pages = new Map([[1, res.items]]);
      this.inFlight.delete(1);
      this.committedKey = key;
      this.total.set(res.total);
      this.libraryTotal.set(res.libraryTotal);
      this.meta.set(res);
      this.loading.set(false);
      this.version.update(v => v + 1);
      this.revision.update(v => v + 1);
      this.queryRevision.update(v => v + 1);
      // Rows past page 1 that are already rendered (e.g. a sort change without scrolling) would otherwise stay empty.
      this.refillRequestedRange();
    } catch (e) {
      if (gen !== this.generation) return;
      this.inFlight.delete(1);
      this.loading.set(false);
      this.onError(e);
    }
  }

  private async loadSinglePage(page: number): Promise<void> {
    const gen = this.generation;
    const query = this.query;
    this.inFlight.add(page);
    try {
      const res = await this.fetchPage(query, page, this.pageSize);
      if (gen !== this.generation) return;
      this.pages.delete(page);
      this.pages.set(page, res.items);
      if (this.reloading) this.loadedDuringReload.add(page);
      this.evictIfNeeded();
      if (res.total !== this.total()) this.total.set(res.total);
      if (res.libraryTotal !== this.libraryTotal()) this.libraryTotal.set(res.libraryTotal);
      this.version.update(v => v + 1);
    } catch (e) {
      if (gen !== this.generation) return;
      this.onError(e);
    } finally {
      if (gen === this.generation) this.inFlight.delete(page);
    }
  }

  /** Loads pages of the last requested range that a commit left out. */
  private refillRequestedRange() {
    if (this.requestedRange) this.ensureRange(this.requestedRange[0], this.requestedRange[1]);
    this.evictIfNeeded();
  }

  private evictIfNeeded() {
    if (this.pages.size <= this.maxCachedPages) return;
    const [activeStart, activeEnd] = this.activeRange;
    for (const page of this.pages.keys()) {
      if (this.pages.size <= this.maxCachedPages) break;
      if (page >= activeStart && page <= activeEnd) continue;
      this.pages.delete(page);
    }
  }
}

/**
 * Angular CDK `DataSource` backed by a signal of virtual row descriptors and a `PagedListCache`.
 * Translates CDK's visible row range (`CollectionViewer.viewChange`) into item indices and requests missing pages.
 */
export class VirtualRowDataSource<TRow> extends DataSource<TRow> {
  private readonly stream = new BehaviorSubject<readonly TRow[]>([]);
  private readonly subscriptions = new Map<CollectionViewer, Subscription>();
  private readonly lastRanges = new Map<CollectionViewer, { start: number; end: number }>();
  private readonly effectRef;

  constructor(
    private readonly rows: Signal<readonly TRow[]>,
    private readonly itemRange: (rows: readonly TRow[], start: number, end: number) => [number, number] | null,
    private readonly ensureRange: (startIndex: number, endIndex: number) => void,
    injector: Injector,
  ) {
    super();
    this.effectRef = effect(() => {
      const current = this.rows();
      this.stream.next(current);
      for (const range of this.lastRanges.values()) {
        this.requestRange(current, range);
      }
    }, { injector });
  }

  connect(collectionViewer: CollectionViewer): Observable<readonly TRow[]> {
    const sub = collectionViewer.viewChange.subscribe(range => {
      this.lastRanges.set(collectionViewer, range);
      this.requestRange(this.rows(), range);
    });
    this.subscriptions.set(collectionViewer, sub);
    return this.stream.asObservable();
  }

  disconnect(collectionViewer: CollectionViewer): void {
    this.subscriptions.get(collectionViewer)?.unsubscribe();
    this.subscriptions.delete(collectionViewer);
    this.lastRanges.delete(collectionViewer);
  }

  destroy() {
    for (const sub of this.subscriptions.values()) sub.unsubscribe();
    this.subscriptions.clear();
    this.lastRanges.clear();
    this.effectRef.destroy();
    this.stream.complete();
  }

  private requestRange(current: readonly TRow[], range: { start: number; end: number }) {
    if (current.length === 0 || range.end <= range.start) return;
    const start = Math.max(0, Math.min(current.length - 1, range.start));
    const end = Math.max(start, Math.min(current.length - 1, range.end - 1));
    const items = this.itemRange(current, start, end);
    if (items) this.ensureRange(items[0], items[1]);
  }
}
