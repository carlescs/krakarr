import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { KeywordSearchPage, parseKeywordIds } from './keyword-search';

const movie = (tmdbId: number) => ({ tmdbId, title: `Movie ${tmdbId}`, year: 2020, runtime: 0 });
const show = (tmdbId: number) => ({ tmdbId, title: `Show ${tmdbId}`, year: 2019 });
const page = <T>(results: T[], p = 1, totalPages = 1) => ({ results, page: p, totalPages });

async function create(inputs: { keywords?: string; media?: string } = {}) {
  const fixture = TestBed.createComponent(KeywordSearchPage);
  if (inputs.keywords !== undefined) fixture.componentRef.setInput('keywords', inputs.keywords);
  if (inputs.media !== undefined) fixture.componentRef.setInput('media', inputs.media);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement, navigate };
}

describe('parseKeywordIds', () => {
  it('keeps distinct positive ids, capped at ten', () => {
    expect(parseKeywordIds(undefined)).toEqual([]);
    expect(parseKeywordIds('4379, 9882,4379,x,-3,0,1.5')).toEqual([4379, 9882]);
    expect(parseKeywordIds('1,2,3,4,5,6,7,8,9,10,11')).toHaveLength(10);
  });
});

describe('KeywordSearchPage', () => {
  it('searches keywords, then lists movies and shows tagged with the picked one', async () => {
    const api = setupTest({
      api: mockApi({
        lookupKeywords: async () => [{ id: 4379, name: 'time travel' }, { id: 9882, name: 'space' }],
        moviesByKeywords: async () => page([movie(1), movie(2)]),
        seriesByKeywords: async () => page([show(9)]),
      }),
    });
    const { fixture, el, navigate } = await create();
    expect(text(el)).toContain('Search for one, then pick it');

    type(el, 'input[name="term"]', ' time travel ');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.lookupKeywords).toHaveBeenCalledWith('time travel');
    expect(el.querySelectorAll('[data-testid="suggestions"] button').length).toBe(2);

    button(el, 'time travel').click();
    await settle(fixture);
    expect(api.moviesByKeywords).toHaveBeenCalledWith([4379], 1);
    expect(api.seriesByKeywords).toHaveBeenCalledWith([4379], 1);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { keywords: '4379', media: null } }));
    expect(text(el, '[data-testid="selected"]')).toContain('time travel');
    // Movies and shows are interleaved.
    expect([...el.querySelectorAll('kr-media-card')].map(c => text(c as HTMLElement))).toEqual([
      expect.stringContaining('Movie 1'), expect.stringContaining('Show 9'), expect.stringContaining('Movie 2'),
    ]);
  });

  it('combines keywords and restarts when one is removed', async () => {
    const api = setupTest({
      api: mockApi({
        lookupKeywords: async () => [{ id: 4379, name: 'time travel' }, { id: 9882, name: 'space' }],
        moviesByKeywords: async () => page([movie(1)]),
        seriesByKeywords: async () => page([]),
      }),
    });
    const { fixture, el } = await create();
    type(el, 'input[name="term"]', 'time');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    button(el, 'time travel').click();
    await settle(fixture);
    button(el, 'space').click();
    await settle(fixture);
    expect(api.moviesByKeywords).toHaveBeenLastCalledWith([4379, 9882], 1);
    expect([...el.querySelectorAll('[data-testid="selected"]')].map(c => text(c as HTMLElement))).toEqual(['time travel ×', 'space ×']);

    button(el, 'Remove time travel').click();
    await settle(fixture);
    expect(api.moviesByKeywords).toHaveBeenLastCalledWith([9882], 1);
    expect(el.querySelectorAll('[data-testid="selected"]').length).toBe(1);
  });

  it('restores keywords from the URL, resolving their names, and filters by media', async () => {
    const api = setupTest({
      api: mockApi({
        keyword: async (id: number) => ({ id, name: 'heist' }),
        moviesByKeywords: async () => page([movie(1)]),
        seriesByKeywords: async () => page([show(9)]),
      }),
    });
    const { fixture, el, navigate } = await create({ keywords: '10051', media: 'series' });
    expect(api.keyword).toHaveBeenCalledWith(10051);
    expect(text(el, '[data-testid="selected"]')).toContain('heist');
    expect(api.moviesByKeywords).not.toHaveBeenCalled();
    expect(api.seriesByKeywords).toHaveBeenCalledWith([10051], 1);
    expect(el.querySelectorAll('kr-media-card').length).toBe(1);

    button(el, 'Movies').click();
    await settle(fixture);
    expect(api.moviesByKeywords).toHaveBeenCalledWith([10051], 1);
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { keywords: '10051', media: 'movies' } }));
    expect(text(el)).toContain('Movie 1');
    expect(text(el)).not.toContain('Show 9');
  });

  it('pages until both lists run out, skipping duplicates', async () => {
    const api = setupTest({
      api: mockApi({
        keyword: async (id: number) => ({ id, name: 'heist' }),
        moviesByKeywords: async (_ids: number[], p: number) => p === 1 ? page([movie(1), movie(2)], 1, 2) : page([movie(2), movie(3)], 2, 2),
        seriesByKeywords: async () => page([show(9)], 1, 1),
      }),
    });
    const { fixture, el } = await create({ keywords: '10051' });
    expect(el.querySelectorAll('kr-media-card').length).toBe(3);

    button(el, 'Load more').click();
    await settle(fixture);
    expect(api.moviesByKeywords).toHaveBeenCalledWith([10051], 2);
    expect(api.seriesByKeywords).toHaveBeenCalledTimes(1);
    expect(el.querySelectorAll('kr-media-card').length).toBe(4);
    expect(text(el)).not.toContain('Load more');
  });

  it('shows errors from either list', async () => {
    setupTest({
      api: mockApi({
        keyword: async (id: number) => ({ id, name: `k${id}` }),
        moviesByKeywords: async () => page([], 1, 0),
        seriesByKeywords: async () => { throw new Error('TMDB is unreachable'); },
      }),
    });
    const { el } = await create({ keywords: '1,2' });
    expect(text(el)).toContain('TMDB is unreachable — open settings');
  });

  it('says when no titles match', async () => {
    setupTest({
      api: mockApi({
        keyword: async (id: number) => ({ id, name: `k${id}` }),
        moviesByKeywords: async () => page([], 1, 0),
        seriesByKeywords: async () => page([], 1, 0),
      }),
    });
    const { el } = await create({ keywords: '1,2' });
    expect(text(el)).toContain('No titles have all of these keywords');
  });
});
