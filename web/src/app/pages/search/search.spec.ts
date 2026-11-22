import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { IndexerQueryStatus, SearchResult } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { SearchPage } from './search';

const result = {
  release: { title: 'Alien.1979.1080p.BluRay', guid: 'g1', size: 1073741824, seeders: 12, peers: 3, indexerName: 'Nyaa', categories: [], indexerId: 1, indexerPriority: 25 },
  quality: 'Bluray-1080p',
} as SearchResult;

describe('SearchPage', () => {
  it('searches indexers and sends a release to downloads', async () => {
    const api = setupTest({
      api: mockApi({
        search: async () => ({
          results: [result],
          errors: [{ indexerId: 2, indexerName: 'Broken', message: 'timeout' }],
          indexers: [
            { indexerId: 1, indexerName: 'Nyaa', state: 'Done', releases: 1, seconds: 0.4 },
            { indexerId: 2, indexerName: 'Broken', state: 'Failed', message: 'timeout', seconds: 60 },
          ],
        }),
        grabRelease: async () => undefined,
        movies: async () => [],
        seriesList: async () => [],
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
    expect(api.search).toHaveBeenCalledWith('alien', 'Movie', expect.any(String));
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

  it('says when nothing was found', async () => {
    setupTest({ api: mockApi({ search: async () => ({ results: [], errors: [], indexers: [] }) }) });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    button(fixture.nativeElement, 'Search').click();
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No results.');
  });

  it('shows which indexers are done, still searching and failed while the search runs', async () => {
    let respond!: (v: unknown) => void;
    const api = setupTest({ api: mockApi({ search: () => new Promise(r => (respond = r)) }) });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Search').click();
    await settle(fixture);
    expect(text(el, '[data-summary]')).toBe('Searching indexers…');

    const searchId = api.search.mock.calls[0][2] as string;
    const events = TestBed.inject(Events);
    const pending: IndexerQueryStatus[] = [
      { indexerId: 1, indexerName: 'Nyaa', state: 'Pending' },
      { indexerId: 2, indexerName: 'Broken', state: 'Pending' },
      { indexerId: 3, indexerName: 'Slow', state: 'Pending' },
    ];
    const done: IndexerQueryStatus[] = [
      { indexerId: 1, indexerName: 'Nyaa', state: 'Done', releases: 4 },
      { indexerId: 2, indexerName: 'Broken', state: 'Failed', message: '401 Unauthorized' },
      pending[2],
    ];
    events.searchProgress.next({ searchId, indexers: done });
    // An older snapshot arriving late doesn't undo finished indexers; other searches are ignored.
    events.searchProgress.next({ searchId, indexers: pending });
    events.searchProgress.next({ searchId: 'other', indexers: pending.map(p => ({ ...p, state: 'Done' as const })) });
    await settle(fixture);

    expect(text(el, '[data-summary]')).toBe('Searching indexers… 2 of 3 done');
    expect([...el.querySelectorAll('[data-state]')].map(c => c.getAttribute('data-state'))).toEqual(['Done', 'Failed', 'Pending']);
    expect(text(el)).toContain('Waiting for Slow');
    expect(text(el, '[data-error]')).toBe('Broken: 401 Unauthorized');

    respond({ results: [], errors: [], indexers: [done[0], done[1], { indexerId: 3, indexerName: 'Slow', state: 'Done', releases: 0 }] });
    await settle(fixture);
    expect(text(el, '[data-summary]')).toBe('3 indexers queried · 1 failed');
    expect(text(el)).not.toContain('Waiting for');
  });
});
