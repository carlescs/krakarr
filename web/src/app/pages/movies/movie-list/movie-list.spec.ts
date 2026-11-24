import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import {
  MovieCollectionGroup,
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

function queryMovies(list: MovieListItem[], q: MovieListQuery = {}, activeDownloadIds = new Set<number>()): {
  items: MovieListItem[];
  groups?: MovieCollectionGroup[];
} {
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

  if (q.groupBy === 'collection') {
    const byCol = new Map<number, MovieListItem[]>();
    const standalone: MovieListItem[] = [];
    for (const m of result) {
      if (m.collectionId != null && collectionNames.has(m.collectionId)) {
        const bucket = byCol.get(m.collectionId) ?? [];
        bucket.push(m);
        byCol.set(m.collectionId, bucket);
      } else {
        standalone.push(m);
      }
    }
    const groups: MovieCollectionGroup[] = [];
    const ordered: MovieListItem[] = [];
    for (const [colId, items] of [...byCol.entries()].sort(([a], [b]) => (collectionNames.get(a) ?? '').localeCompare(collectionNames.get(b) ?? ''))) {
      items.sort((a, b) => a.year - b.year || a.title.localeCompare(b.title));
      groups.push({ key: `c-${colId}`, collectionId: colId, title: collectionNames.get(colId)!, count: items.length, downloaded: items.filter(m => m.hasFile).length });
      ordered.push(...items);
    }
    if (standalone.length > 0) {
      groups.push({ key: 'other', collectionId: null, title: 'Other movies', count: standalone.length, downloaded: standalone.filter(m => m.hasFile).length });
      ordered.push(...standalone);
    }
    return { items: ordered, groups };
  }

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
        const { items, groups } = queryMovies(list, q, downloading);
        const page = q.page ?? 1;
        const pageSize = q.pageSize ?? 60;
        const start = (page - 1) * pageSize;
        return {
          items: items.slice(start, start + pageSize),
          page,
          pageSize,
          total: items.length,
          libraryTotal: list.length,
          groups,
        };
      },
      movieIds: async (q: Omit<MovieListQuery, 'page' | 'pageSize'> = {}) => {
        const events = TestBed.inject(Events);
        const downloading = new Set(events.downloads().filter(d => d.movieId && !d.imported).map(d => d.movieId!));
        return queryMovies(list, q, downloading).items.map(m => m.id);
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

  it('virtually paginates grouped movie grid views across pages', async () => {
    localStorage.setItem('krakarr.movies.groupBy', 'collection');
    // 10 movies in Alien Collection + 70 standalone movies = 80 total (2 pages of 60)
    const many = [
      ...Array.from({ length: 10 }, (_, i) => movie({ id: i + 1, title: `Alien ${i + 1}`, year: 1979 + i, collectionId: 10 })),
      ...Array.from({ length: 70 }, (_, i) => movie({ id: 100 + i, title: `Standalone ${String(i + 1).padStart(2, '0')}`, year: 2020 })),
    ];
    const { api, fixture, el } = await render(many);
    expect(api.movies).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 60, groupBy: 'collection' }));
    expect([...el.querySelectorAll('section h2')].map(h => h.textContent)).toEqual(['Alien Collection', 'Other movies']);

    // Scroll into page 2 (rows in the Other movies group past index 60).
    const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport)).componentInstance as CdkVirtualScrollViewport;
    viewport.setRenderedRange({ start: 13, end: 18 });
    await settle(fixture);
    expect(api.movies).toHaveBeenCalledWith(expect.objectContaining({ page: 2, pageSize: 60, groupBy: 'collection' }));
    expect(titles(el).some(t => t?.startsWith('Standalone 6'))).toBe(true);
  });

  it('guides plain users to Discover when the library is empty', async () => {
    const { el } = await render([], 'User');
    expect(text(el)).toContain('Your movie library is empty');
    expect(el.querySelector('a[href="/discover/search?type=movies"]')?.textContent).toBe('Search & request');
    expect(el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
