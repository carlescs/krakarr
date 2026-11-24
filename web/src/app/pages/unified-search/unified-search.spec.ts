import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { MockApi, button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { UnifiedSearchPage } from './unified-search';

const lookups = (overrides: Record<string, (...args: never[]) => unknown> = {}) => mockApi({
  lookupMovies: async () => [
    { tmdbId: 603, title: 'The Matrix', year: 1999, originalTitle: 'Matrix', runtime: 136 },
    { tmdbId: 604, title: 'The Matrix Reloaded', year: 2003, runtime: 138, movieId: 9 },
  ],
  lookupSeries: async () => [{ tmdbId: 1396, title: 'Breaking Bad', year: 2008, network: 'AMC', status: 'Ended' }],
  lookupPeople: async () => [{ tmdbId: 6384, name: 'Keanu Reeves', knownForDepartment: 'Acting', knownFor: ['The Matrix', 'John Wick'] }],
  lookupKeywords: async () => [{ id: 4379, name: 'time travel' }],
  ...overrides,
});

async function create(api: MockApi, inputs: { q?: string; type?: string } = {}, role: 'Admin' | 'User' = 'Admin') {
  setupTest({ api, role });
  const fixture = TestBed.createComponent(UnifiedSearchPage);
  if (inputs.q !== undefined) fixture.componentRef.setInput('q', inputs.q);
  if (inputs.type !== undefined) fixture.componentRef.setInput('type', inputs.type);
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  await settle(fixture);
  const el = fixture.nativeElement as HTMLElement;
  const search = async (term: string) => {
    type(el, 'input[name="term"]', term);
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
  };
  const tab = async (label: string) => {
    ([...el.querySelectorAll('[role="tab"]')] as HTMLElement[]).find(t => t.textContent!.trim().startsWith(label))!.click();
    await settle(fixture);
  };
  return { fixture, el, navigate, search, tab };
}

describe('UnifiedSearchPage', () => {
  it('looks up movies, series, people and keywords at once and previews them all', async () => {
    const api = lookups();
    const { el, navigate, search } = await create(api);
    expect(text(el)).toContain('Search TMDB for something to add');

    await search(' matrix ');
    for (const lookup of [api.lookupMovies, api.lookupSeries, api.lookupPeople, api.lookupKeywords]) expect(lookup).toHaveBeenCalledWith('matrix');
    expect(navigate).toHaveBeenCalledWith([], expect.objectContaining({ queryParams: { q: 'matrix', type: null } }));
    expect([...el.querySelectorAll('[role="tab"]')].map(t => text(t as HTMLElement))).toEqual(['All', 'Movies2', 'Series1', 'People1', 'Keywords1']);
    expect([...el.querySelectorAll('kr-media-row h2')].map(h => h.textContent)).toEqual(['Movies', 'Series']);
    expect(el.querySelectorAll('kr-media-card').length).toBe(3);
    expect(el.querySelector('a[href="/people/6384"]')?.textContent).toContain('Keanu Reeves');
    expect(el.querySelector('a[href="/keywords?keywords=4379"]')?.textContent).toContain('time travel');
  });

  it('lists movies in full with add buttons, and marks titles in the library', async () => {
    const { fixture, el, navigate, search, tab } = await create(lookups());
    await search('matrix');
    await tab('Movies');
    expect(navigate).toHaveBeenLastCalledWith([], expect.objectContaining({ queryParams: { q: 'matrix', type: 'movies' } }));
    expect(el.querySelectorAll('[data-testid="title-result"]').length).toBe(2);
    expect(text(el, '[data-testid="title-result"] .text-xs')).toBe('Matrix'); // original title
    expect(el.querySelector('a.btn-secondary[href="/movies/9"]')?.textContent).toContain('In library');

    button(el, 'Add').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-movie-modal')).not.toBeNull();
  });

  it('opens the series add dialog from the series tab', async () => {
    const { fixture, el, search, tab } = await create(lookups());
    await search('breaking');
    await tab('Series');
    expect(text(el)).toContain('AMC · Ended');
    button(el, 'Add').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-series-modal')).not.toBeNull();
  });

  it('lets plain users request titles instead', async () => {
    const api = lookups({ createRequest: async () => ({ id: 1, tmdbId: 603, title: 'The Matrix' }) });
    const { fixture, el, search, tab } = await create(api, {}, 'User');
    expect(text(el)).toContain('something to request');
    await search('matrix');
    await tab('Movies');
    button(el, 'Request').click();
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Request').click();
    await settle(fixture);
    expect(api.createRequest).toHaveBeenCalledWith('Movie', 603);
    expect(el.querySelector('a[href="/requests"]')?.textContent).toContain('Requested');
  });

  it('restores the search and tab from the URL', async () => {
    const api = lookups();
    const { el } = await create(api, { q: 'keanu', type: 'people' });
    expect(api.lookupPeople).toHaveBeenCalledWith('keanu');
    expect((el.querySelector('input[name="term"]') as HTMLInputElement).value).toBe('keanu');
    expect(el.querySelector('a[href="/people/6384"]')?.textContent).toContain('Known for: The Matrix, John Wick');
    expect(el.querySelector('[role="tab"][aria-selected="true"]')?.textContent).toContain('People');
  });

  it('shows a failed lookup next to the ones that worked', async () => {
    const { el, search, tab } = await create(lookups({ lookupSeries: async () => { throw new Error('TheTVDB API key is not configured'); } }));
    await search('matrix');
    expect(text(el)).toContain('TheTVDB API key is not configured — open settings');
    expect(text(el)).toContain('The Matrix');
    await tab('Series');
    expect(text(el, '.card')).toContain('TheTVDB API key is not configured');
  });

  it('says when nothing was found', async () => {
    const empty = async () => [];
    const { el, search, tab } = await create(lookups({ lookupMovies: empty, lookupSeries: empty, lookupPeople: empty, lookupKeywords: empty }));
    await search('zzz');
    expect(text(el)).toContain('Nothing found for “zzz”.');
    await tab('Movies');
    expect(text(el)).toContain('No movies found.');
  });
});
