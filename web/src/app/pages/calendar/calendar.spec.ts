import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { calendarRouteMatcher, toIsoDate } from '../../core/calendar';
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

async function render(view: 'agenda' | 'month', extra: Record<string, (...args: never[]) => unknown> = {}, role: 'Admin' | 'User' = 'Admin', stored: object = {},
  url = '/calendar') {
  localStorage.setItem('krakarr.calendar', JSON.stringify({ view, ...stored }));
  const api = setupTest({
    role, api: mockApi({ calendar: async () => entries, subscribers: async () => [], ...extra }),
    providers: [provideRouter([{ matcher: calendarRouteMatcher, component: CalendarPage }])],
  });
  const harness = await RouterTestingHarness.create();
  await harness.navigateByUrl(url, CalendarPage);
  const fixture = harness.fixture;
  await settle(fixture);
  return { api, fixture, harness, router: TestBed.inject(Router), el: fixture.nativeElement as HTMLElement };
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

  it('puts the view and period in the URL', async () => {
    const { fixture, router, el } = await render('month');
    expect(router.url).toBe(`/calendar/month/${today.slice(0, 7)}`);
    button(el, 'Week').click();
    await settle(fixture);
    expect(router.url).toMatch(/^\/calendar\/week\/\d{4}-\d{2}-\d{2}$/);
    button(el, 'Agenda').click();
    await settle(fixture);
    expect(router.url).toBe(`/calendar/agenda/${today.slice(0, 7)}`);
  });

  it('opens the view and month in the URL and follows URL changes', async () => {
    const { api, fixture, harness, el } = await render('agenda', {}, 'Admin', {}, '/calendar/month/2025-02');
    expect(button(el, 'Next month')).toBeTruthy();
    const [start, end] = api.calendar.mock.calls.at(-1)! as [string, string];
    expect(start > '2025-01-25' && start <= '2025-02-01' && end >= '2025-02-28' && end < '2025-03-08').toBe(true);
    await harness.navigateByUrl('/calendar/week/2025-03-10');
    await settle(fixture);
    expect(button(el, 'Next week')).toBeTruthy();
    const [weekStart, weekEnd] = api.calendar.mock.calls.at(-1)! as [string, string];
    expect(weekStart <= '2025-03-10' && '2025-03-10' <= weekEnd && weekStart > '2025-03-03').toBe(true);
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
