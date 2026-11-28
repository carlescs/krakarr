import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ReleaseSearch, byApprovalQualitySeeders } from './indexer-search';
import { ReleaseInfo, SearchStreamEvent } from './models';

interface Row { release: ReleaseInfo; approved: boolean; qualityWeight: number }

const row = (guid: string, qualityWeight: number, approved = true, seeders = 0, indexerId = 1): Row => ({
  release: { title: guid, guid, size: 1, seeders, categories: [], indexerId, indexerName: 'X', indexerPriority: 25 },
  approved, qualityWeight,
});

@Component({ selector: 'kr-host', template: '' })
class Host { readonly search = new ReleaseSearch<Row>(); }

function create() {
  TestBed.configureTestingModule({});
  const fixture = TestBed.createComponent(Host);
  return { fixture, search: fixture.componentInstance.search };
}

describe('ReleaseSearch', () => {
  it('ranks rows as they arrive, replaces duplicates and applies the final ranking', async () => {
    const { search } = create();
    let emit!: (e: SearchStreamEvent<Row>) => void;
    let finish!: () => void;
    const running = search.run(onEvent => new Promise<void>(r => { emit = onEvent; finish = r; }));
    expect(search.running()).toBe(true);

    emit({ type: 'indexers', indexers: [{ indexerId: 1, indexerName: 'A', state: 'Pending' }, { indexerId: 2, indexerName: 'B', state: 'Pending' }] });
    emit({ type: 'releases', indexer: { indexerId: 1, indexerName: 'A', state: 'Done', releases: 3 }, releases: [row('low', 1), row('rejected', 9, false), row('high', 5)] });
    expect(search.results().map(r => r.release.guid)).toEqual(['high', 'low', 'rejected']);
    expect(search.indexers().map(s => s.state)).toEqual(['Done', 'Pending']);

    emit({ type: 'releases', indexer: { indexerId: 2, indexerName: 'B', state: 'Done', releases: 1 }, releases: [row('low2', 1, true, 50)], replaced: ['low'] });
    expect(search.results().map(r => r.release.guid)).toEqual(['high', 'low2', 'rejected']);

    emit({ type: 'done', indexers: [], order: ['low2', 'high', 'rejected'] });
    finish();
    await running;
    expect(search.results().map(r => r.release.guid)).toEqual(['low2', 'high', 'rejected']);
    expect(search.running()).toBe(false);
  });

  it('cancelling aborts the request, ignores late events and does not reject', async () => {
    const { search } = create();
    let emit!: (e: SearchStreamEvent<Row>) => void;
    let signal!: AbortSignal;
    const running = search.run((onEvent, s) => new Promise<void>((_, reject) => {
      emit = onEvent;
      signal = s;
      s.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    }));
    search.cancel();
    await expect(running).resolves.toBeUndefined();
    expect(signal.aborted).toBe(true);
    emit({ type: 'releases', releases: [row('late', 1)] });
    expect(search.results()).toEqual([]);
    expect(search.running()).toBe(false);
  });

  it('a new search cancels the previous one', async () => {
    const { search } = create();
    let first!: AbortSignal;
    void search.run((_, s) => { first = s; return new Promise<void>(() => {}); });
    void search.run(() => new Promise<void>(() => {}));
    expect(first.aborted).toBe(true);
    expect(search.running()).toBe(true);
  });

  it('destroying the owner cancels the search', () => {
    const { fixture, search } = create();
    let signal!: AbortSignal;
    void search.run((_, s) => { signal = s; return new Promise<void>(() => {}); });
    fixture.destroy();
    expect(signal.aborted).toBe(true);
  });

  it('filters the rows by the selected indexers and clears the filter on a new search', async () => {
    const { search } = create();
    await search.run(async onEvent => onEvent({ type: 'releases', releases: [row('a', 3, true, 0, 1), row('b', 2, true, 0, 2), row('c', 1, true, 0, 3)] }));
    expect(search.filtered().map(r => r.release.guid)).toEqual(['a', 'b', 'c']);

    search.toggle(1);
    search.toggle(3);
    expect(search.filtered().map(r => r.release.guid)).toEqual(['a', 'c']);
    search.toggle(1);
    expect(search.filtered().map(r => r.release.guid)).toEqual(['c']);

    await search.run(async () => {});
    expect(search.selected().size).toBe(0);
  });
  it('orders by quality, then custom format score, then seeders while indexers answer', () => {
    const scored = (guid: string, qualityWeight: number, customFormatScore: number, seeders: number) => ({ ...row(guid, qualityWeight, true, seeders), customFormatScore });
    const rows = [scored('plain', 17, 0, 500), scored('atmos', 17, 100, 1), scored('web', 16, 500, 900)];
    expect(rows.sort(byApprovalQualitySeeders).map(r => r.release.guid)).toEqual(['atmos', 'plain', 'web']);
  });
});
