import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, describe, expect, it } from 'vitest';
import { SeriesListItem, SeriesListPage as SeriesPageDto, SeriesListQuery, UserRole } from '../../../core/models';
import { SEARCH_DEBOUNCE_MS } from '../../../core/paged-list';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { SeriesListPage } from './series-list';

const show = (s: Partial<SeriesListItem>): SeriesListItem => ({
  id: 1, title: 'Show', year: 2020, status: 'Continuing', monitored: true, episodeCount: 10, episodeFileCount: 10, downloadingCount: 0, ...s,
});
const shows = [
  show({ id: 1, title: 'Severance', episodeFileCount: 4, downloadingCount: 2 }),
  show({ id: 2, title: 'Dark', status: 'Ended' }),
  show({ id: 3, title: 'Lost', status: 'Ended', monitored: false, episodeFileCount: 0 }),
];

function queryShows(list: SeriesListItem[], q: SeriesListQuery = {}): SeriesListItem[] {
  const textFilter = q.q?.toLowerCase().trim() ?? '';
  let result = list.filter(s => {
    if (textFilter && !s.title.toLowerCase().includes(textFilter)) return false;
    switch (q.filter) {
      case 'missing': return s.monitored && s.episodeFileCount < s.episodeCount;
      case 'continuing': return s.status !== 'Ended';
      case 'ended': return s.status === 'Ended';
      case 'unmonitored': return !s.monitored;
      default: return true;
    }
  });
  const dir = q.desc ? -1 : 1;
  if (q.sort === 'episodes') {
    const pct = (s: SeriesListItem) => (s.episodeCount === 0 ? 0 : s.episodeFileCount / s.episodeCount);
    result = [...result].sort((a, b) => (pct(a) - pct(b)) * dir);
  } else if (q.sort === 'year') {
    result = [...result].sort((a, b) => (a.year - b.year) * dir);
  } else if (q.sort === 'status') {
    result = [...result].sort((a, b) => (Number(a.status === 'Ended') - Number(b.status === 'Ended')) * dir);
  } else if (q.desc) {
    result = [...result].reverse();
  }
  return result;
}

async function render(list: SeriesListItem[], role: UserRole = 'Admin') {
  const api = setupTest({
    role,
    providers: [{ provide: SEARCH_DEBOUNCE_MS, useValue: 0 }],
    api: mockApi({
      seriesList: async (q: SeriesListQuery = {}): Promise<SeriesPageDto> => {
        const filtered = queryShows(list, q);
        const page = q.page ?? 1;
        const pageSize = q.pageSize ?? 60;
        const start = (page - 1) * pageSize;
        return {
          items: filtered.slice(start, start + pageSize),
          page,
          pageSize,
          total: filtered.length,
          libraryTotal: list.length,
        };
      },
      seriesIds: async (q: Omit<SeriesListQuery, 'page' | 'pageSize'> = {}) => queryShows(list, q).map(s => s.id),
    }),
  });
  const fixture = TestBed.createComponent(SeriesListPage);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

const titles = (el: HTMLElement) => [...el.querySelectorAll('a[href^="/series/"] .truncate.text-xs.font-medium')].map(e => e.textContent);

describe('SeriesListPage', () => {
  afterEach(() => localStorage.clear());

  it('shows a sortable table with bulk selection', async () => {
    const { fixture, el } = await render(shows);
    button(el, 'Table').click();
    await settle(fixture);
    const rows = () => [...el.querySelectorAll('tbody a')].map(a => a.textContent);
    expect(rows()).toEqual(['Severance', 'Dark', 'Lost']);
    expect(text(el, 'tbody tr')).toContain('4/10');

    button(el, 'Episodes').click();
    await settle(fixture);
    expect(rows()).toEqual(['Lost', 'Severance', 'Dark']);

    (el.querySelector('input[aria-label="Select all"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(el, 'kr-bulk-actions')).toContain('3 selected');
    expect(text(el, 'kr-bulk-actions')).toContain('Search missing');
  });

  it('lists the library with episode counts and downloads', async () => {
    const { el } = await render(shows);
    expect(text(el, 'h1')).toBe('Series 3');
    expect(titles(el)).toEqual(['Severance', 'Dark', 'Lost']);
    expect(text(el.querySelector('a[href="/series/1"]') as HTMLElement)).toContain('↓ 2');
    expect(text(el.querySelector('a[href="/series/1"]') as HTMLElement)).toContain('4/10');
  });

  it('filters by text and state', async () => {
    const { fixture, el } = await render(shows);
    type(el, 'select', 'missing');
    await settle(fixture);
    expect(titles(el)).toEqual(['Severance']);
    type(el, 'select', 'ended');
    await settle(fixture);
    expect(titles(el)).toEqual(['Dark', 'Lost']);
    type(el, 'input[type="search"]', 'lo');
    await settle(fixture);
    expect(titles(el)).toEqual(['Lost']);
    type(el, 'input[type="search"]', 'nothing');
    await settle(fixture);
    expect(text(el)).toContain('No series match the filter.');
  });

  it('fetches additional pages on demand as the virtual viewport scrolls', async () => {
    localStorage.setItem('krakarr.series.view', 'table');
    const many = Array.from({ length: 130 }, (_, i) => show({ id: i + 1, title: `Show ${String(i + 1).padStart(3, '0')}` }));
    const { api, fixture, el } = await render(many);
    expect(text(el, 'h1')).toBe('Series 130');
    expect(api.seriesList).toHaveBeenCalledTimes(1);
    expect(api.seriesList).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 60 }));

    // Simulate scrolling into page 2 via CDK's rendered range.
    const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport)).componentInstance as CdkVirtualScrollViewport;
    viewport.setRenderedRange({ start: 65, end: 85 });
    await settle(fixture);
    expect(api.seriesList).toHaveBeenCalledWith(expect.objectContaining({ page: 2, pageSize: 60 }));
    expect([...el.querySelectorAll('tbody a')].map(a => a.textContent)).toContain('Show 070');

    // "Select all" fetches all 130 matching ids from /series/ids.
    (el.querySelector('input[aria-label="Select all"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(api.seriesIds).toHaveBeenCalledTimes(1);
    expect(text(el, 'kr-bulk-actions')).toContain('130 selected');
  });

  it('guides staff to add series and plain users to Discover', async () => {
    const staff = await render([]);
    expect(staff.el.querySelector('a[href="/discover/search?type=series"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const user = await render([], 'User');
    expect(user.el.querySelector('a[href="/discover/search?type=series"]')?.textContent).toBe('Search & request');
    expect(user.el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
