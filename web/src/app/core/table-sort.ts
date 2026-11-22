import { Signal, computed, signal } from '@angular/core';

/** What a column sorts by. `null`/`undefined` (unknown) always go last, whatever the direction. */
export type SortValue = string | number | boolean | null | undefined;

/**
 * A sortable column: the value to sort by, or that plus `desc` when the first click should sort descending (sizes,
 * dates, seeders). `'source'` sorts by the order the rows come in, for a column the API already sorts by.
 */
export type SortColumn<T> = ((row: T) => SortValue) | { value: (row: T) => SortValue; desc?: boolean } | 'source';

export interface SortState<K extends string> { key: K | null; desc: boolean }

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

function compare(a: SortValue, b: SortValue): number {
  if (typeof a === 'string' && typeof b === 'string') return collator.compare(a, b);
  return Number(a) - Number(b);
}

/**
 * Click-to-sort for a table. `rows` is `source` in the chosen order; with no column chosen it is `source` as given
 * (the API order). Clicking a header sorts by it, clicking again reverses it, and a third click goes back to the
 * default. The sort is stable, so ties keep the source order, and it only reorders: rows are never copied.
 */
export class TableSort<T, K extends string = string> {
  private readonly initial: SortState<K>;
  readonly state = signal<SortState<K>>({ key: null, desc: false });

  readonly rows: Signal<readonly T[]>;

  constructor(source: Signal<readonly T[]>, private readonly columns: Record<K, SortColumn<T>>, initial?: { key: NoInfer<K>; desc?: boolean }) {
    this.initial = { key: initial?.key ?? null, desc: initial?.desc ?? false };
    this.state.set(this.initial);
    this.rows = computed(() => {
      const list = source();
      const { key, desc } = this.state();
      if (key === null) return list;
      const column: SortColumn<T> = this.columns[key];
      if (column === 'source') return desc ? [...list].reverse() : list;
      const value = typeof column === 'object' ? column.value : column;
      // Compute each value once rather than on every comparison.
      const keyed = list.map((row, i) => ({ row, i, v: value(row) }));
      const sign = desc ? -1 : 1;
      keyed.sort((a, b) => {
        const aNull = a.v === null || a.v === undefined || Number.isNaN(a.v);
        const bNull = b.v === null || b.v === undefined || Number.isNaN(b.v);
        if (aNull || bNull) return aNull === bNull ? a.i - b.i : aNull ? 1 : -1;
        return sign * compare(a.v, b.v) || a.i - b.i;
      });
      return keyed.map(k => k.row);
    });
  }

  /** Sorts by `key`: first in its natural direction, then reversed, then back to the default order. */
  toggle(key: K) {
    const s = this.state();
    const column: SortColumn<T> = this.columns[key];
    const first = typeof column === 'object' && !!column.desc;
    if (s.key !== key) this.state.set({ key, desc: first });
    else if (s.desc === first) this.state.set({ key, desc: !first });
    else if (this.initial.key === key) this.state.set({ key, desc: first });
    else this.state.set(this.initial);
  }

  /** The `aria-sort` value of a header. */
  ariaSort(key: K): 'ascending' | 'descending' | 'none' {
    const s = this.state();
    return s.key !== key ? 'none' : s.desc ? 'descending' : 'ascending';
  }
}
