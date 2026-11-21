import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import { MovieListItem, TorrentStatus, UserRole } from '../../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { MovieListPage } from './movie-list';

const movie = (m: Partial<MovieListItem>): MovieListItem => ({ id: 1, title: 'Movie', year: 2000, monitored: true, hasFile: true, ...m });
const movies = [
  movie({ id: 1, title: 'Alien', year: 1979, collectionId: 10, quality: 'Bluray-1080p' }),
  movie({ id: 2, title: 'Aliens', year: 1986, collectionId: 10, hasFile: false }),
  movie({ id: 3, title: 'Arrival', year: 2016, hasFile: false, monitored: false }),
];
const collections = [{ id: 10, tmdbId: 8091, name: 'Alien Collection' }];

async function render(list: MovieListItem[], role: UserRole = 'Admin') {
  setupTest({ role, api: mockApi({ movies: async () => list, movieCollections: async () => collections }) });
  const fixture = TestBed.createComponent(MovieListPage);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

const titles = (el: HTMLElement) => [...el.querySelectorAll('a[href^="/movies/"] .truncate.font-medium')].map(e => e.textContent);

describe('MovieListPage', () => {
  afterEach(() => localStorage.clear());

  it('lists and filters the library', async () => {
    const { fixture, el } = await render(movies);
    expect(text(el, 'h1')).toBe('Movies 3');
    expect(titles(el)).toEqual(['Alien', 'Aliens', 'Arrival']);
    expect(text(el.querySelector('a[href="/movies/1"]') as HTMLElement)).toContain('1979 · Bluray-1080p');

    type(el, 'select', 'missing');
    await settle(fixture);
    expect(titles(el)).toEqual(['Aliens']);
    type(el, 'select', 'unmonitored');
    await settle(fixture);
    expect(titles(el)).toEqual(['Arrival']);
    type(el, 'select', 'all');
    type(el, 'input[type="search"]', 'alien collection');
    await settle(fixture);
    expect(titles(el)).toEqual(['Alien', 'Aliens']);
  });

  it('groups by collection and collapses groups', async () => {
    const { fixture, el } = await render(movies);
    type(el, 'select[aria-label="Group by"]', 'collection');
    await settle(fixture);
    expect([...el.querySelectorAll('section h2')].map(h => h.textContent)).toEqual(['Alien Collection', 'Other movies']);
    expect(text(el)).toContain('1 / 2 downloaded');
    expect(localStorage.getItem('krakarr.movies.groupBy')).toBe('collection');

    button(el, 'Alien Collection').click();
    await settle(fixture);
    expect(titles(el)).toEqual(['Arrival']);
  });

  it('shows live download progress', async () => {
    const { fixture, el } = await render(movies);
    const events = TestBed.inject(Events);
    events.connected.set(true);
    events.downloads.set([{ movieId: 2, progress: 30, imported: false } as TorrentStatus]);
    type(el, 'select', 'downloading');
    await settle(fixture);
    expect(titles(el)).toEqual(['Aliens']);
    expect(el.querySelector('kr-progress')).not.toBeNull();
  });

  it('guides plain users to Discover when the library is empty', async () => {
    const { el } = await render([], 'User');
    expect(text(el)).toContain('Your movie library is empty');
    expect(el.querySelector('a[href="/movies/add"]')?.textContent).toBe('Search & request');
    expect(el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
