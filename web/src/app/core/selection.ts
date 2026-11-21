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

/**
 * A multi-selection of library titles. Only titles in `scope` (the filtered list) count as selected, so changing the
 * filter never leaves hidden titles in a bulk action; titles that come back into view are selected again.
 */
export class Selection {
  private readonly ids = signal<ReadonlySet<number>>(new Set());
  /** Last title toggled without Shift: the anchor of a Shift+click range. */
  private anchor: number | null = null;

  constructor(private readonly scope: Signal<readonly { id: number }[]>) {}

  /** Selected ids in list order. */
  readonly selected = computed(() => {
    const ids = this.ids();
    return ids.size === 0 ? [] : this.scope().filter(x => ids.has(x.id)).map(x => x.id);
  });
  readonly count = computed(() => this.selected().length);
  readonly all = computed(() => this.scope().length > 0 && this.count() === this.scope().length);

  has(id: number): boolean { return this.ids().has(id); }

  /** Toggles one title; with `range`, sets every title between the last toggled one and this one to its new state. */
  toggle(id: number, range = false) {
    const next = new Set(this.ids());
    const select = !next.has(id);
    const list = this.scope();
    const from = range && this.anchor !== null ? list.findIndex(x => x.id === this.anchor) : -1;
    const to = list.findIndex(x => x.id === id);
    if (from >= 0 && to >= 0) {
      for (let i = Math.min(from, to); i <= Math.max(from, to); i++) {
        if (select) next.add(list[i].id); else next.delete(list[i].id);
      }
    } else {
      if (select) next.add(id); else next.delete(id);
    }
    this.anchor = id;
    this.ids.set(next);
  }

  /** Selects every title in scope, or clears the selection when they all are. */
  toggleAll() {
    if (this.all()) this.clear();
    else this.ids.set(new Set(this.scope().map(x => x.id)));
  }

  clear() {
    this.anchor = null;
    this.ids.set(new Set());
  }
}
