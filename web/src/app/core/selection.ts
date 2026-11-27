import { Signal, computed, signal } from '@angular/core';

/** How a library page shows its titles. */
export type ViewMode = 'grid' | 'table';

/** The view mode last chosen on a page (per browser); grid when none or storage is unavailable. */
export function storedView(key: string): ViewMode {
  try { return localStorage.getItem(key) === 'table' ? 'table' : 'grid'; } catch { return 'grid'; }
}

export function storeView(key: string, mode: ViewMode) {
  try { localStorage.setItem(key, mode); } catch { /* storage unavailable */ }
}

type Key = string | number;

/** Common selection contract consumed by `kr-bulk-actions` and library list headers. */
export interface SelectionModel<K extends Key = number> {
  readonly selected: Signal<K[]>;
  readonly count: Signal<number>;
  readonly all: Signal<boolean>;
  has(id: K): boolean;
  toggle(id: K, range?: boolean): void | Promise<void>;
  toggleAll(): void | Promise<void>;
  clear(): void;
}

/**
 * A multi-selection of list items (library titles, torrents). Only items in `scope` (the filtered list) count as
 * selected, so changing the filter never leaves hidden items in a bulk action; items that come back into view are
 * selected again. Items are identified by `key`, their `id` by default.
 */
export class Selection<T = { id: number }, K extends Key = number> implements SelectionModel<K> {
  private readonly ids = signal<ReadonlySet<K>>(new Set());
  /** Last item toggled without Shift: the anchor of a Shift+click range. */
  private anchor: K | null = null;
  private readonly keys: Signal<K[]>;

  constructor(scope: Signal<readonly T[]>, key: (item: T) => K = item => (item as { id: K }).id) {
    this.keys = computed(() => scope().map(key));
  }

  /** Selected keys in list order. */
  readonly selected = computed(() => {
    const ids = this.ids();
    return ids.size === 0 ? [] : this.keys().filter(k => ids.has(k));
  });
  readonly count = computed(() => this.selected().length);
  readonly all = computed(() => this.keys().length > 0 && this.count() === this.keys().length);

  has(id: K): boolean { return this.ids().has(id); }

  /** Toggles one item; with `range`, sets every item between the last toggled one and this one to its new state. */
  toggle(id: K, range = false) {
    const next = new Set(this.ids());
    const select = !next.has(id);
    const keys = this.keys();
    const from = range && this.anchor !== null ? keys.indexOf(this.anchor) : -1;
    const to = keys.indexOf(id);
    if (from >= 0 && to >= 0) {
      for (let i = Math.min(from, to); i <= Math.max(from, to); i++) {
        if (select) next.add(keys[i]); else next.delete(keys[i]);
      }
    } else {
      if (select) next.add(id); else next.delete(id);
    }
    this.anchor = id;
    this.ids.set(next);
  }

  /** Selects every item in scope, or clears the selection when they all are. */
  toggleAll() {
    if (this.all()) this.clear();
    else this.ids.set(new Set(this.keys()));
  }

  clear() {
    this.anchor = null;
    this.ids.set(new Set());
  }
}

/**
 * Multi-selection for a paginated list where not all matching items are in memory at once.
 * Single toggles and Shift+clicks across loaded pages are synchronous; "Select all" or Shift+clicks across an
 * unloaded gap fetch the full matching id list on demand via `fetchAllIds`.
 */
export class PagedSelection implements SelectionModel<number> {
  private readonly ids = signal<ReadonlySet<number>>(new Set());
  private anchor: number | null = null;
  private generation = 0;
  /** Size of the id list when the server capped "Select all" below `total`; selecting that many then counts as all. */
  private readonly cap = signal(Infinity);

  readonly selected = computed(() => [...this.ids()]);
  readonly count = computed(() => this.ids().size);
  readonly all = computed(() => {
    const total = this.total();
    return total > 0 && this.ids().size >= Math.min(total, this.cap());
  });

  constructor(
    private readonly total: Signal<number>,
    private readonly loadedIds: () => number[],
    private readonly allLoaded: () => boolean,
    private readonly idsBetween: (a: number, b: number) => number[] | null,
    private readonly fetchAllIds: () => Promise<number[]>,
    private readonly onError?: (e: unknown) => void,
    /** Called when "Select all" got fewer ids than `total` (the server caps bulk id lists). */
    private readonly onTruncated?: (selected: number, total: number) => void,
  ) {}

  has(id: number): boolean { return this.ids().has(id); }

  toggle(id: number, range = false): void | Promise<void> {
    const current = this.ids();
    const select = !current.has(id);
    if (!range || this.anchor === null) {
      const next = new Set(current);
      if (select) next.add(id); else next.delete(id);
      this.anchor = id;
      this.ids.set(next);
      return;
    }

    const anchor = this.anchor;
    const local = this.idsBetween(anchor, id);
    if (local) {
      const next = new Set(current);
      for (const item of local) if (select) next.add(item); else next.delete(item);
      this.anchor = id;
      this.ids.set(next);
      return;
    }

    const gen = this.generation;
    this.anchor = id;
    return this.fetchAllIds().then(all => {
      if (gen !== this.generation) return;
      const from = all.indexOf(anchor);
      const to = all.indexOf(id);
      const next = new Set(this.ids());
      if (from >= 0 && to >= 0) {
        for (let i = Math.min(from, to); i <= Math.max(from, to); i++) {
          if (select) next.add(all[i]); else next.delete(all[i]);
        }
      } else {
        if (select) next.add(id); else next.delete(id);
      }
      this.ids.set(next);
    }, e => this.onError?.(e));
  }

  toggleAll(): void | Promise<void> {
    if (this.all()) {
      this.clear();
      return;
    }
    if (this.total() === 0) return;
    if (this.allLoaded()) {
      this.ids.set(new Set(this.loadedIds()));
      return;
    }
    const gen = this.generation;
    return this.fetchAllIds().then(all => {
      if (gen !== this.generation) return;
      const total = this.total();
      if (all.length < total) {
        this.cap.set(all.length);
        this.onTruncated?.(all.length, total);
      }
      this.ids.set(new Set(all));
    }, e => this.onError?.(e));
  }

  /** Narrows the current selection to the active filter's matching items after a filter/search change. */
  reconcile(): void | Promise<void> {
    const gen = ++this.generation;
    this.anchor = null;
    this.cap.set(Infinity);
    const current = this.ids();
    if (current.size === 0) return;
    if (this.total() === 0) {
      this.ids.set(new Set());
      return;
    }
    if (this.allLoaded()) {
      const allowed = new Set(this.loadedIds());
      const next = new Set<number>();
      for (const id of current) if (allowed.has(id)) next.add(id);
      this.ids.set(next);
      return;
    }
    return this.fetchAllIds().then(all => {
      if (gen !== this.generation) return;
      const allowed = new Set(all);
      const next = new Set<number>();
      for (const id of this.ids()) if (allowed.has(id)) next.add(id);
      this.ids.set(next);
    }, e => this.onError?.(e));
  }

  clear() {
    this.generation++;
    this.anchor = null;
    this.ids.set(new Set());
  }
}
