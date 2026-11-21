import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { toIsoDate } from '../../core/calendar';
import { CalendarEntry } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { CalendarPage } from './calendar';

const today = toIsoDate(new Date());
const entry = (e: Partial<CalendarEntry>): CalendarEntry => ({
  kind: 'Episode', date: today, isRegionDate: true, title: 'Severance', year: 2022, runtime: 50, monitored: true, hasFile: false, downloading: false, ...e,
});
const entries = [
  entry({ seriesId: 4, episodeId: 1, seasonNumber: 2, episodeNumber: 1, episodeTitle: 'Hello, Ms. Cobel', network: 'Apple TV+' }),
  entry({ kind: 'Movie', movieId: 3, title: 'Arrival', releaseType: 'Digital', hasFile: true }),
];

async function render(view: 'agenda' | 'month', extra: Record<string, (...args: never[]) => unknown> = {}, role: 'Admin' | 'User' = 'Admin', stored: object = {}) {
  localStorage.setItem('krakarr.calendar', JSON.stringify({ view, ...stored }));
  const api = setupTest({ role, api: mockApi({ calendar: async () => entries, subscribers: async () => [], ...extra }) });
  const fixture = TestBed.createComponent(CalendarPage);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('CalendarPage', () => {
  afterEach(() => localStorage.clear());

  it('lists today’s releases in the agenda', async () => {
    const { api, el } = await render('agenda');
    expect(api.calendar).toHaveBeenCalled();
    const [start, end] = api.calendar.mock.calls[0] as [string, string];
    expect(start <= today && today <= end).toBe(true);
    expect(text(el)).toContain('Today');
    expect(el.querySelector('a[href="/series/4"]')?.textContent).toContain('Severance');
    expect(text(el)).toContain('Hello, Ms. Cobel · Apple TV+');
    expect(el.querySelector('a[href="/movies/3"]')?.textContent).toContain('Arrival');
  });

  it('hides kinds that are toggled off and remembers it', async () => {
    const { fixture, el } = await render('agenda');
    (el.querySelector('button[aria-pressed]') as HTMLButtonElement).click(); // Series
    await settle(fixture);
    expect(el.querySelector('a[href="/series/4"]')).toBeNull();
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
    expect(JSON.parse(localStorage.getItem('krakarr.calendar')!).filters.series).toBe(false);
  });

  it('shows staff the whole library and plain users the titles they follow', async () => {
    const admin = await render('agenda');
    expect(admin.api.calendar.mock.calls[0][2]).toBeNull();
    TestBed.resetTestingModule();
    localStorage.clear();
    const user = await render('agenda', {}, 'User');
    expect(user.api.calendar.mock.calls[0][2]).toBe('me');
  });

  it('ignores the "my titles" default saved by earlier versions', async () => {
    const { api } = await render('agenda', {}, 'Admin', { owner: 'me' });
    expect(api.calendar.mock.calls[0][2]).toBeNull();
  });

  it('offers to show everything when nothing followed is in the month', async () => {
    const { api, fixture, el } = await render('month', { calendar: async () => [] }, 'User');
    expect(text(el)).toContain('Nothing you follow in');
    button(el, 'show everything').click();
    await settle(fixture);
    expect(api.calendar.mock.calls.at(-1)![2]).toBeNull();
  });

  it('moves between periods and views', async () => {
    const { api, fixture, el } = await render('month');
    const calls = api.calendar.mock.calls.length;
    button(el, 'Next month').click();
    await settle(fixture);
    expect(api.calendar.mock.calls.length).toBe(calls + 1);
    button(el, 'Week').click();
    await settle(fixture);
    expect(button(el, 'Next week')).toBeTruthy();
  });

  it('shows the iCal feed URL with the API key', async () => {
    const { api, fixture, el } = await render('agenda', { settings: async () => ({ apiKey: 'secret-key' }) });
    button(el, 'iCal feed').click();
    await settle(fixture);
    expect(api.settings).toHaveBeenCalledWith('general');
    const url = (el.querySelector('[role="dialog"] input[readonly]') as HTMLInputElement).value;
    expect(url).toContain('secret-key');
  });
});
