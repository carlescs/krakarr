import { DestroyRef, Signal, computed, inject, signal } from '@angular/core';
import { IndexerQueryStatus, ReleaseInfo, SearchStreamEvent } from './models';

/** Starts a streamed search: calls `onEvent` for each line and resolves when the stream ends. */
export type SearchStarter<T> = (onEvent: (e: SearchStreamEvent<T>) => void, signal: AbortSignal) => Promise<void>;

/** Interim order while indexers are still answering: approved first, then better quality, custom format score, then more seeders. */
export function byApprovalQualitySeeders(a: { approved?: boolean; qualityWeight?: number; customFormatScore?: number; release: ReleaseInfo },
  b: { approved?: boolean; qualityWeight?: number; customFormatScore?: number; release: ReleaseInfo }): number {
  return Number(b.approved ?? true) - Number(a.approved ?? true)
    || (b.qualityWeight ?? 0) - (a.qualityWeight ?? 0)
    || (b.customFormatScore ?? 0) - (a.customFormatScore ?? 0)
    || (b.release.seeders ?? 0) - (a.release.seeders ?? 0);
}

/**
 * An interactive search whose releases arrive as each indexer answers (`SearchStream` on the server). Tracks which
 * indexers have answered, which are still running and which failed, and keeps the rows ranked: by `interimOrder`
 * while results come in, then by the server's ranking once every indexer is done. Create it in a field initializer
 * (it needs an injection context); destroying the component cancels a running search.
 *
 * It also holds the indexer filter: clicking indexer chips in `kr-indexer-progress` selects them, and `filtered`
 * keeps the rows from the selected indexers (all of them when none is selected).
 */
export class ReleaseSearch<T extends { release: ReleaseInfo }> {
  /** Every indexer in the current (or last) search; empty until the server reports. */
  readonly indexers = signal<IndexerQueryStatus[]>([]);
  /** The rows so far, best first. */
  readonly results = signal<T[]>([]);
  readonly running = signal(false);
  /** Indexers whose results are shown; empty shows every indexer. */
  readonly selected = signal<ReadonlySet<number>>(new Set());
  /** `results` from the selected indexers, or all of them when no indexer is selected. */
  readonly filtered: Signal<T[]> = computed(() => {
    const rows = this.results();
    const selected = this.selected();
    return selected.size === 0 ? rows : rows.filter(r => selected.has(r.release.indexerId));
  });
  private abort: AbortController | null = null;

  constructor(private readonly interimOrder: (a: T, b: T) => number = byApprovalQualitySeeders) {
    inject(DestroyRef).onDestroy(() => this.cancel());
  }

  /** Runs a search, cancelling the previous one. Rejects if it fails, but not when cancelled. */
  async run(start: SearchStarter<T>): Promise<void> {
    this.cancel();
    const abort = new AbortController();
    this.abort = abort;
    this.indexers.set([]);
    this.results.set([]);
    this.selected.set(new Set());
    this.running.set(true);
    try {
      await start(e => {
        if (!abort.signal.aborted) this.apply(e);
      }, abort.signal);
    } catch (e) {
      if (!abort.signal.aborted) throw e;
    } finally {
      if (this.abort === abort) {
        this.abort = null;
        this.running.set(false);
      }
    }
  }

  /** Stops the running search, keeping what arrived so far. */
  cancel() {
    this.abort?.abort();
    this.abort = null;
    this.running.set(false);
  }

  /** Cancels and forgets everything. */
  reset() {
    this.cancel();
    this.indexers.set([]);
    this.results.set([]);
    this.selected.set(new Set());
  }

  /** Adds an indexer to the filter, or removes it if it was already there. */
  toggle(indexerId: number) {
    this.selected.update(old => {
      const next = new Set(old);
      if (!next.delete(indexerId)) next.add(indexerId);
      return next;
    });
  }

  clearFilter() {
    this.selected.set(new Set());
  }

  private apply(e: SearchStreamEvent<T>) {
    switch (e.type) {
      case 'indexers':
        this.indexers.set(e.indexers ?? []);
        break;
      case 'releases': {
        const status = e.indexer;
        if (status) this.indexers.update(list => list.map(s => s.indexerId === status.indexerId ? status : s));
        const replaced = new Set(e.replaced ?? []);
        const added = e.releases ?? [];
        if (replaced.size || added.length) {
          this.results.update(rows => [...rows.filter(r => !replaced.has(r.release.guid)), ...added].sort(this.interimOrder));
        }
        break;
      }
      case 'done': {
        if (e.indexers) this.indexers.set(e.indexers);
        const rank = new Map((e.order ?? []).map((guid, i) => [guid, i]));
        this.results.update(rows => [...rows].sort((a, b) => (rank.get(a.release.guid) ?? rank.size) - (rank.get(b.release.guid) ?? rank.size)));
        break;
      }
    }
  }
}
