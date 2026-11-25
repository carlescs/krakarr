import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import {
  MovieCollectionSummary,
  MovieFilter,
  MovieListItem,
  MovieListPage as MoviePageDto,
  MovieListQuery,
  TorrentStatus,
  UserRole,
} from '../../../core/models';
import { SEARCH_DEBOUNCE_MS } from '../../../core/paged-list';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { MovieListPage } from './movie-list';

const movie = (m: Partial<MovieListItem>): MovieListItem => ({ id: 1, title: 'Movie', year: 2000, monitored: true, hasFile: true, ...m });
const movies = [
  movie({ id: 1, title: 'Alien', year: 1979, collectionId: 10, quality: 'Bluray-1080p' }),
  movie({ id: 2, title: 'Aliens', year: 1986, collectionId: 10, hasFile: false }),
  movie({ id: 3, title: 'Arrival', year: 2016, hasFile: false, monitored: false }),
];
const collectionNames = new Map<number, string>([[10, 'Alien Collection']]);

function queryMovies(list: MovieListItem[], q: MovieListQuery = {}, activeDownloadIds = new Set<number>()): { items: MovieListItem[] } {
  const textFilter = q.q?.toLowerCase().trim() ?? '';
  let result = list.filter(m => {
    if (textFilter) {
      const colName = m.collectionId != null ? (collectionNames.get(m.collectionId) ?? '').toLowerCase() : '';
      if (!m.title.toLowerCase().includes(textFilter) && !colName.includes(textFilter)) return false;
    }
    switch (q.filter) {
      case 'missing': return m.monitored && !m.hasFile;
      case 'downloading': return m.downloadProgress != null || activeDownloadIds.has(m.id);
      case 'downloaded': return m.hasFile;
      case 'unmonitored': return !m.monitored;
      default: return true;
    }
  });

  const dir = q.desc ? -1 : 1;
  if (q.sort === 'year') {
    result = [...result].sort((a, b) => (a.year - b.year) * dir);
  } else if (q.sort === 'status') {
    const rank = (m: MovieListItem) => (m.hasFile ? 3 : m.downloadProgress != null ? 2 : !m.monitored ? 1 : 0);
    result = [...result].sort((a, b) => (rank(a) - rank(b)) * dir);
  } else if (q.desc) {
    result = [...result].reverse();
  }
  return { items: result };
}

async function render(list: MovieListItem[], role: UserRole = 'Admin') {
  const api = setupTest({
    role,
    providers: [{ provide: SEARCH_DEBOUNCE_MS, useValue: 0 }],
    api: mockApi({
      movies: async (q: MovieListQuery = {}): Promise<MoviePageDto> => {
        const events = TestBed.inject(Events);
        const downloading = new Set(events.downloads().filter(d => d.movieId && !d.imported).map(d => d.movieId!));
        const { items } = queryMovies(list, q, downloading);
        const page = q.page ?? 1;
        const pageSize = q.pageSize ?? 60;
        const start = (page - 1) * pageSize;
        return {
          items: items.slice(start, start + pageSize),
          page,
          pageSize,
          total: items.length,
          libraryTotal: list.length,
        };
      },
      movieIds: async (q: Omit<MovieListQuery, 'page' | 'pageSize'> = {}) => {
        const events = TestBed.inject(Events);
        const downloading = new Set(events.downloads().filter(d => d.movieId && !d.imported).map(d => d.movieId!));
        return queryMovies(list, q, downloading).items.map(m => m.id);
      },
      movieCollections: async (q: { q?: string; filter?: MovieFilter } = {}): Promise<MovieCollectionSummary[]> => {
        const matching = queryMovies(list, q).items.filter(m => m.collectionId != null && collectionNames.has(m.collectionId));
        return [...collectionNames].flatMap(([id, name]) => {
          const inCollection = matching.filter(m => m.collectionId === id);
          return inCollection.length
            ? [{ id, tmdbId: id * 100, name, count: inCollection.length, downloaded: inCollection.filter(m => m.hasFile).length }]
            : [];
        });
      },
      subscriptions: async () => [{ mediaType: 'Movie', libraryId: 3, title: 'Arrival', year: 2016, subscribedAt: '' }],
    }),
  });
  const fixture = TestBed.createComponent(MovieListPage);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
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

  it('shows collections as cards that open the collection', async () => {
    const { api, fixture, el } = await render(movies);
    expect(button(el, 'Movies').getAttribute('aria-pressed')).toBe('true');
    button(el, 'Collections').click();
    await settle(fixture);
    expect(button(el, 'Collections').getAttribute('aria-pressed')).toBe('true');
    expect(localStorage.getItem('krakarr.movies.groupBy')).toBe('collection');
    const card = el.querySelector('a[href="/movies/collections/10"]') as HTMLElement;
    expect(text(card)).toContain('Alien Collection');
    expect(text(card)).toContain('1 / 2 downloaded');
    expect(el.querySelector('a[href="/movies/3"]')).toBeNull();
    expect([...el.querySelectorAll('button')].some(b => b.textContent?.trim() === 'Select')).toBe(false);

    // The list's filter narrows the collections.
    type(el, 'select', 'unmonitored');
    await settle(fixture);
    expect(api.movieCollections).toHaveBeenLastCalledWith({ q: undefined, filter: 'unmonitored' });
    expect(text(el)).toContain('No collection has movies matching the filter.');

    // The table view lists them too.
    type(el, 'select', 'all');
    button(el, 'Table').click();
    await settle(fixture);
    expect(text(el.querySelector('tbody a[href="/movies/collections/10"]') as HTMLElement)).toBe('Alien Collection');
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

  it('shows a sortable table and remembers the view', async () => {
    const { fixture, el } = await render(movies);
    button(el, 'Table').click();
    await settle(fixture);
    expect(localStorage.getItem('krakarr.movies.view')).toBe('table');
    const rows = () => [...el.querySelectorAll('tbody tr')].map(r => text(r as HTMLElement));
    expect(rows()[0]).toContain('Alien');
    expect(rows()[0]).toContain('Downloaded');
    expect(rows()[2]).toContain('Unmonitored');
    expect(rows()[2]).toContain('Following');

    button(el, 'Year').click();
    button(el, 'Year').click();
    await settle(fixture);
    expect([...el.querySelectorAll('tbody a')].map(a => a.textContent)).toEqual(['Arrival', 'Aliens', 'Alien']);
    expect(el.querySelector('th[aria-sort="descending"]')).not.toBeNull();
  });

  it('selects rows and shows the bulk toolbar', async () => {
    localStorage.setItem('krakarr.movies.view', 'table');
    const { fixture, el } = await render(movies);
    expect(el.querySelector('kr-bulk-actions')).toBeNull();

    (el.querySelector('input[aria-label="Select Alien"]') as HTMLInputElement).click();
    (el.querySelector('input[aria-label="Select Arrival"]') as HTMLInputElement).dispatchEvent(new MouseEvent('click', { shiftKey: true }));
    await settle(fixture);
    expect(text(el, 'kr-bulk-actions')).toContain('3 selected');

    // Filtering narrows what bulk actions apply to.
    type(el, 'select', 'missing');
    await settle(fixture);
    expect(text(el, 'kr-bulk-actions')).toContain('1 selected');
  });

  it('selects cards in grid selection mode instead of opening them', async () => {
    const { fixture, el } = await render(movies);
    button(el, 'Select').click();
    await settle(fixture);
    expect(text(el, 'kr-bulk-actions')).toContain('0 selected');

    const card = () => el.querySelectorAll<HTMLElement>('a.group')[1];
    expect(card().hasAttribute('href')).toBe(false);
    card().click();
    await settle(fixture);
    expect(card().getAttribute('aria-checked')).toBe('true');
    card().dispatchEvent(new KeyboardEvent('keydown', { key: ' ' }));
    await settle(fixture);
    expect(card().getAttribute('aria-checked')).toBe('false');
    card().click();
    await settle(fixture);
    expect(text(el, 'kr-bulk-actions')).toContain('1 selected');

    button(el, 'Done').click();
    await settle(fixture);
    expect(el.querySelector('kr-bulk-actions')).toBeNull();
    expect(card().getAttribute('href')).toBe('/movies/2');
  });

  it('virtually paginates large movie libraries and supports cross-page select all', async () => {
    localStorage.setItem('krakarr.movies.view', 'table');
    const many = Array.from({ length: 140 }, (_, i) => movie({ id: i + 1, title: `Movie ${String(i + 1).padStart(3, '0')}` }));
    const { api, fixture, el } = await render(many);
    expect(text(el, 'h1')).toBe('Movies 140');
    expect(api.movies).toHaveBeenCalledTimes(1);

    const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport)).componentInstance as CdkVirtualScrollViewport;
    viewport.setRenderedRange({ start: 70, end: 90 });
    await settle(fixture);
    expect(api.movies).toHaveBeenCalledWith(expect.objectContaining({ page: 2, pageSize: 60 }));
    expect([...el.querySelectorAll('tbody a')].map(a => a.textContent)).toContain('Movie 075');

    (el.querySelector('input[aria-label="Select all"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(api.movieIds).toHaveBeenCalledTimes(1);
    expect(text(el, 'kr-bulk-actions')).toContain('140 selected');
  });

  it('guides plain users to Discover when the library is empty', async () => {
    const { el } = await render([], 'User');
    expect(text(el)).toContain('Your movie library is empty');
    expect(el.querySelector('a[href="/discover/search?type=movies"]')?.textContent).toBe('Search & request');
    expect(el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
