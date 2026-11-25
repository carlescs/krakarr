import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { MovieCollectionDetails, MovieListItem, MovieLookup, UserRole } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { CollectionDetailPage, collectionEntries } from './collection-detail';

const movie = (m: Partial<MovieListItem>): MovieListItem => ({ id: 1, title: 'Movie', year: 2000, monitored: true, hasFile: true, ...m });
const collection: MovieCollectionDetails = {
  id: 5, tmdbId: 119, name: 'The Lord of the Rings Collection', posterUrl: null, fanartUrl: null,
  movies: [
    movie({ id: 1, title: 'The Fellowship of the Ring', year: 2001, quality: 'Bluray-1080p' }),
    movie({ id: 3, title: 'The Return of the King', year: 2003, hasFile: false }),
  ],
};
const missing: MovieLookup[] = [{ tmdbId: 121, title: 'The Two Towers', year: 2002, runtime: 0 }];

async function render(role: UserRole = 'Admin') {
  const api = setupTest({ role, api: mockApi({ movieCollection: async () => collection, movieCollectionMissing: async () => missing }) });
  const fixture = TestBed.createComponent(CollectionDetailPage);
  fixture.componentRef.setInput('id', '5');
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

const cardTitles = (el: HTMLElement) =>
  [...el.querySelectorAll('a[href^="/movies/"] .truncate.font-medium, kr-media-card .truncate.font-medium')].map(e => e.textContent?.trim());

describe('CollectionDetailPage', () => {
  afterEach(() => localStorage.clear());

  it('lists the library movies of the collection', async () => {
    const { api, el } = await render();
    expect(api.movieCollection).toHaveBeenCalledWith(5);
    expect(api.movieCollectionMissing).not.toHaveBeenCalled();
    expect(text(el, 'h1')).toBe('The Lord of the Rings Collection');
    expect(text(el)).toContain('2 movies in the library · 1 downloaded');
    expect(cardTitles(el)).toEqual(['The Fellowship of the Ring', 'The Return of the King']);
    expect(el.querySelector('a[href="/movies/1"]')).not.toBeNull();
  });

  it('shows missing movies in release order with a switch and remembers it', async () => {
    const { api, fixture, el } = await render();
    const toggle = el.querySelector('button[role="switch"]') as HTMLButtonElement;
    expect(toggle.getAttribute('aria-checked')).toBe('false');
    toggle.click();
    await settle(fixture);
    expect(api.movieCollectionMissing).toHaveBeenCalledWith(5);
    expect(toggle.getAttribute('aria-checked')).toBe('true');
    expect(cardTitles(el)).toEqual(['The Fellowship of the Ring', 'The Two Towers', 'The Return of the King']);
    expect(el.querySelector('kr-media-card a[href="/movies/tmdb/121"]')).not.toBeNull();
    expect(text(el)).toContain('1 missing');
    expect(localStorage.getItem('krakarr.collections.showMissing')).toBe('true');

    toggle.click();
    await settle(fixture);
    expect(cardTitles(el)).toEqual(['The Fellowship of the Ring', 'The Return of the King']);
  });

  it('offers plain users to request a missing movie', async () => {
    localStorage.setItem('krakarr.collections.showMissing', 'true');
    const { el } = await render('User');
    expect(cardTitles(el)).toContain('The Two Towers');
    expect(() => button(el, 'Request')).not.toThrow();
  });

  it('reports TMDB errors for missing movies without hiding the library ones', async () => {
    localStorage.setItem('krakarr.collections.showMissing', 'true');
    setupTest({ api: mockApi({ movieCollection: async () => collection, movieCollectionMissing: async () => { throw new Error('TMDB API key is not configured'); } }) });
    const fixture = TestBed.createComponent(CollectionDetailPage);
    fixture.componentRef.setInput('id', '5');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Couldn’t load the missing movies');
    expect(cardTitles(el)).toEqual(['The Fellowship of the Ring', 'The Return of the King']);
  });
});

describe('collectionEntries', () => {
  it('merges library and missing movies by year, unknown years last', () => {
    const entries = collectionEntries(
      [movie({ id: 1, title: 'B', year: 2005 }), movie({ id: 2, title: 'Unknown', year: 0 })],
      [{ tmdbId: 9, title: 'A', year: 2005, runtime: 0 }, { tmdbId: 8, title: 'Old', year: 1990, runtime: 0 }],
    );
    expect(entries.map(e => e.key)).toEqual(['t8', 't9', 'm1', 'm2']);
  });
});
