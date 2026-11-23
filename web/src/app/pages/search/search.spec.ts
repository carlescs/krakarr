import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { IndexerQueryStatus, SearchResult, SearchStreamEvent } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { SearchPage } from './search';

const result = {
  release: { title: 'Alien.1979.1080p.BluRay', guid: 'g1', size: 1073741824, seeders: 12, peers: 3, indexerName: 'Nyaa', categories: [], indexerId: 1, indexerPriority: 25 },
  quality: 'Bluray-1080p',
} as SearchResult;

type Emit = (e: SearchStreamEvent<SearchResult>) => void;

/** A `searchStream` mock that replays `events` and finishes. */
function replay(...events: SearchStreamEvent<SearchResult>[]) {
  return async (_q: string, _t: string, onEvent: Emit) => { for (const e of events) onEvent(e); };
}

const nyaa: IndexerQueryStatus = { indexerId: 1, indexerName: 'Nyaa', state: 'Done', releases: 1, seconds: 0.4 };

describe('SearchPage', () => {
  it('searches indexers and sends a release to downloads', async () => {
    const broken: IndexerQueryStatus = { indexerId: 2, indexerName: 'Broken', state: 'Failed', message: 'timeout', seconds: 60 };
    const api = setupTest({
      api: mockApi({
        searchStream: replay(
          { type: 'indexers', indexers: [{ ...nyaa, state: 'Pending' }, { ...broken, state: 'Pending' }] },
          { type: 'releases', indexer: nyaa, releases: [result] },
          { type: 'releases', indexer: broken, releases: [] },
          { type: 'done', indexers: [nyaa, broken], order: ['g1'] },
        ),
        grabRelease: async () => undefined,
        movies: async () => ({ items: [], page: 1, pageSize: 200, total: 0, libraryTotal: 0 }),
        seriesList: async () => ({ items: [], page: 1, pageSize: 200, total: 0, libraryTotal: 0 }),
      }),
    });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="q"]', 'alien');
    type(el, 'select[name="type"]', 'Movie');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.searchStream).toHaveBeenCalledWith('alien', 'Movie', expect.any(Function), expect.any(AbortSignal));
    expect(text(el)).toContain('2 indexers queried · 1 failed');
    expect(text(el)).toContain('Broken: timeout');
    expect(text(el)).toContain('Alien.1979.1080p.BluRay');
    expect(text(el)).toContain('1.0 GB');

    button(el, 'Download').click();
    await settle(fixture);
    expect(text(el, 'h2')).toBe('Download release');
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Download').click();
    await settle(fixture);
    expect(api.grabRelease).toHaveBeenCalledWith(result.release, 'manual', {});
    expect(el.querySelector('[role="dialog"]')).toBeNull();
  });

  it('sorts results by a column', async () => {
    const small = { ...result, release: { ...result.release, title: 'Alien.1979.720p', guid: 'g2', size: 1024, seeders: 40 } } as SearchResult;
    setupTest({
      api: mockApi({
        searchStream: replay(
          { type: 'indexers', indexers: [{ ...nyaa, state: 'Pending' }] },
          { type: 'releases', indexer: nyaa, releases: [small, result] },
          { type: 'done', indexers: [nyaa], order: ['g2', 'g1'] },
        ),
      }),
    });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Search').click();
    await settle(fixture);
    const titles = () => [...el.querySelectorAll('tbody tr td:first-child > div:first-child')].map(d => d.textContent);
    expect(titles()).toEqual(['Alien.1979.720p', 'Alien.1979.1080p.BluRay']);

    // Sizes sort largest first.
    button(el, 'Size').click();
    await settle(fixture);
    expect(titles()).toEqual(['Alien.1979.1080p.BluRay', 'Alien.1979.720p']);
    expect(el.querySelector('th[aria-sort="descending"]')?.textContent).toContain('Size');
  });

  it('says when nothing was found', async () => {
    setupTest({ api: mockApi({ searchStream: replay({ type: 'indexers', indexers: [] }, { type: 'done', indexers: [], order: [] }) }) });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    button(fixture.nativeElement, 'Search').click();
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No results.');
  });

  it('shows results and indexer progress while the search runs', async () => {
    let emit!: Emit;
    let finish!: () => void;
    const api = setupTest({
      api: mockApi({ searchStream: (_q: string, _t: string, onEvent: Emit) => new Promise<void>(r => { emit = onEvent; finish = r; }) }),
    });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.searchStream).toHaveBeenCalled();
    expect(text(el, '[data-summary]')).toBe('Searching indexers…');

    const pending: IndexerQueryStatus[] = [
      { indexerId: 1, indexerName: 'Nyaa', state: 'Pending' },
      { indexerId: 2, indexerName: 'Broken', state: 'Pending' },
      { indexerId: 3, indexerName: 'Slow', state: 'Pending' },
    ];
    emit({ type: 'indexers', indexers: pending });
    emit({ type: 'releases', indexer: { ...pending[0], state: 'Done', releases: 1 }, releases: [result] });
    emit({ type: 'releases', indexer: { ...pending[1], state: 'Failed', message: '401 Unauthorized' }, releases: [] });
    await settle(fixture);

    expect(text(el, '[data-summary]')).toBe('Searching indexers… 2 of 3 done');
    expect([...el.querySelectorAll('[data-state]')].map(c => c.getAttribute('data-state'))).toEqual(['Done', 'Failed', 'Pending']);
    expect(text(el)).toContain('Waiting for Slow');
    expect(text(el, '[data-error]')).toBe('Broken: 401 Unauthorized');
    // Rows are already there, and grabbable, before the search ends.
    expect(text(el)).toContain('Alien.1979.1080p.BluRay');
    expect(text(el)).not.toContain('No results.');

    // The slow indexer has the same release from a preferred indexer: it takes the row's place.
    const better = { ...result, release: { ...result.release, guid: 'g9', indexerName: 'Slow', seeders: 99 } } as SearchResult;
    emit({ type: 'releases', indexer: { ...pending[2], state: 'Done', releases: 1 }, releases: [better], replaced: ['g1'] });
    emit({ type: 'done', indexers: [{ ...pending[0], state: 'Done' }, { ...pending[1], state: 'Failed', message: '401 Unauthorized' }, { ...pending[2], state: 'Done' }], order: ['g9'] });
    finish();
    await settle(fixture);
    expect(text(el, '[data-summary]')).toBe('3 indexers queried · 1 failed');
    expect(text(el)).not.toContain('Waiting for');
    expect(el.querySelectorAll('tbody tr').length).toBe(1);
    expect(text(el, 'tbody')).toContain('Slow');
  });

  it('searches only the selected indexers when one or more are picked', async () => {
    const api = setupTest({
      api: mockApi({
        searchIndexers: async () => [
          { id: 1, name: 'Nyaa' },
          { id: 2, name: '1337x' },
          { id: 3, name: 'TorrentLeech' },
        ],
        searchStream: replay(
          { type: 'indexers', indexers: [{ ...nyaa, state: 'Pending' }] },
          { type: 'releases', indexer: nyaa, releases: [result] },
          { type: 'done', indexers: [nyaa], order: ['g1'] },
        ),
      }),
    });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const input = el.querySelector<HTMLInputElement>('input[name="q"]')!;

    expect(input.placeholder).toBe('Search all enabled indexers…');

    // Open the picker and select one indexer.
    button(el, 'All indexers').click();
    await settle(fixture);
    const checkboxes = () => [...el.querySelectorAll<HTMLInputElement>('kr-multi-picker input[type="checkbox"]')];
    checkboxes()[0].click();
    await settle(fixture);

    expect(input.placeholder).toBe('Search Nyaa…');
    type(el, 'input[name="q"]', 'alien');
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.searchStream).toHaveBeenLastCalledWith('alien', 'Generic', expect.any(Function), expect.any(AbortSignal), [1]);

    // Pick a second indexer and search again.
    button(el, 'Nyaa').click();
    await settle(fixture);
    checkboxes()[2].click();
    await settle(fixture);

    expect(input.placeholder).toBe('Search 2 selected indexers…');
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.searchStream).toHaveBeenLastCalledWith('alien', 'Generic', expect.any(Function), expect.any(AbortSignal), [1, 3]);
  });
});
