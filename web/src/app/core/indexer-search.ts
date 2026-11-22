import { DestroyRef, inject, signal } from '@angular/core';
import { Events } from './events';
import { IndexerQueryStatus } from './models';

/** A random id for one search. Not `crypto.randomUUID`, which needs a secure context (plain http on the LAN isn't one). */
function newSearchId(): string {
  return Math.random().toString(36).slice(2) + Date.now().toString(36);
}

/**
 * Merges a progress snapshot into what we have. Snapshots can arrive out of order, so an indexer that already
 * finished never goes back to pending.
 */
export function mergeStatuses(current: IndexerQueryStatus[], incoming: IndexerQueryStatus[]): IndexerQueryStatus[] {
  const known = new Map(current.map(s => [s.indexerId, s]));
  return incoming.map(s => {
    const old = known.get(s.indexerId);
    return old && old.state !== 'Pending' && s.state === 'Pending' ? old : s;
  });
}

/**
 * Tracks which indexers an interactive search has queried, which are still running and which failed, from the
 * server's `searchProgress` events. Create it in a field initializer (it needs an injection context):
 *
 *   protected readonly indexerSearch = new IndexerSearch();
 *   const searchId = this.indexerSearch.start();
 *   const res = await this.api.movieReleases(id, searchId);
 *   this.indexerSearch.finish(res.indexers);
 */
export class IndexerSearch {
  /** Every indexer in the current (or last) search; empty until the server reports. */
  readonly indexers = signal<IndexerQueryStatus[]>([]);
  private current: string | null = null;

  constructor() {
    const sub = inject(Events).searchProgress.subscribe(p => {
      if (p.searchId === this.current) this.indexers.update(old => mergeStatuses(old, p.indexers));
    });
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
  }

  /** Starts tracking a new search; pass the returned id to the API. */
  start(): string {
    this.current = newSearchId();
    this.indexers.set([]);
    return this.current;
  }

  /** Stops listening and takes the final statuses from the response (authoritative, events may still be in flight). */
  finish(indexers?: IndexerQueryStatus[]) {
    this.current = null;
    if (indexers) this.indexers.set(indexers);
  }

  reset() {
    this.current = null;
    this.indexers.set([]);
  }
}
