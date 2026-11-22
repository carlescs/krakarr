import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { setupTest } from '../../testing/test-setup';
import { IndexerSearch, mergeStatuses } from './indexer-search';

describe('mergeStatuses', () => {
  it('takes newer states but never moves a finished indexer back to pending', () => {
    const merged = mergeStatuses(
      [{ indexerId: 1, indexerName: 'A', state: 'Done', releases: 3 }, { indexerId: 2, indexerName: 'B', state: 'Pending' }],
      [{ indexerId: 1, indexerName: 'A', state: 'Pending' }, { indexerId: 2, indexerName: 'B', state: 'Failed', message: 'x' }, { indexerId: 3, indexerName: 'C', state: 'Pending' }],
    );
    expect(merged.map(s => s.state)).toEqual(['Done', 'Failed', 'Pending']);
    expect(merged[0].releases).toBe(3);
  });
});

describe('IndexerSearch filter', () => {
  it('keeps the rows of the selected indexers, all of them when none is selected, and resets on a new search', () => {
    setupTest();
    const search = TestBed.runInInjectionContext(() => new IndexerSearch());
    const rows = signal([1, 2, 1, 3].map((indexerId, i) => ({ id: i, release: { indexerId } })));
    const filtered = search.filter(rows);
    expect(filtered().length).toBe(4);

    search.toggle(1);
    expect(filtered().map(r => r.id)).toEqual([0, 2]);
    search.toggle(3);
    expect(filtered().map(r => r.id)).toEqual([0, 2, 3]);
    search.toggle(1);
    expect(filtered().map(r => r.id)).toEqual([3]);

    search.start();
    expect(search.selected().size).toBe(0);
    expect(filtered().length).toBe(4);
  });
});
