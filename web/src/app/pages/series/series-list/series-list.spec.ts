import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SeriesListItem, UserRole } from '../../../core/models';
import { mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { SeriesListPage } from './series-list';

const show = (s: Partial<SeriesListItem>): SeriesListItem => ({
  id: 1, title: 'Show', year: 2020, status: 'Continuing', monitored: true, episodeCount: 10, episodeFileCount: 10, downloadingCount: 0, ...s,
});
const shows = [
  show({ id: 1, title: 'Severance', episodeFileCount: 4, downloadingCount: 2 }),
  show({ id: 2, title: 'Dark', status: 'Ended' }),
  show({ id: 3, title: 'Lost', status: 'Ended', monitored: false, episodeFileCount: 0 }),
];

async function render(list: SeriesListItem[], role: UserRole = 'Admin') {
  setupTest({ role, api: mockApi({ seriesList: async () => list }) });
  const fixture = TestBed.createComponent(SeriesListPage);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

const titles = (el: HTMLElement) => [...el.querySelectorAll('a[href^="/series/"] .truncate.text-xs.font-medium')].map(e => e.textContent);

describe('SeriesListPage', () => {
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

  it('guides staff to add series and plain users to Discover', async () => {
    const staff = await render([]);
    expect(staff.el.querySelector('a[href="/series/add"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const user = await render([], 'User');
    expect(user.el.querySelector('a[href="/series/add"]')?.textContent).toBe('Search & request');
    expect(user.el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
