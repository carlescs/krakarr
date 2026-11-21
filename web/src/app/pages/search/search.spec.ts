import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SearchResult } from '../../core/models';
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
        search: async () => ({ results: [result], errors: [{ indexerId: 2, indexerName: 'Broken', message: 'timeout' }] }),
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
    expect(api.search).toHaveBeenCalledWith('alien', 'Movie');
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
    setupTest({ api: mockApi({ search: async () => ({ results: [], errors: [] }) }) });
    const fixture = TestBed.createComponent(SearchPage);
    await settle(fixture);
    button(fixture.nativeElement, 'Search').click();
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No results.');
  });
});
