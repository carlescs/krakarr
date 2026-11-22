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

/**
 * A multi-selection of list items (library titles, torrents). Only items in `scope` (the filtered list) count as
 * selected, so changing the filter never leaves hidden items in a bulk action; items that come back into view are
 * selected again. Items are identified by `key`, their `id` by default.
 */
export class Selection<T = { id: number }, K extends Key = number> {
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
