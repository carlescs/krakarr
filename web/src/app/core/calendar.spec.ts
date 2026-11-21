import { describe, expect, it } from 'vitest';
import {
  DEFAULT_FILTERS, buildItems, episodeRangeLabel, feedUrl, groupByDay, itemState, parseIsoDate, shiftAnchor, toIsoDate, viewRange,
} from './calendar';
import { CalendarEntry } from './models';

const movie = (over: Partial<CalendarEntry> = {}): CalendarEntry => ({
  kind: 'Movie', date: '2026-11-20', releaseType: 'Digital', country: 'US', isRegionDate: false, movieId: 1, title: 'Film', year: 2026,
  runtime: 100, monitored: true, hasFile: false, downloading: false, ...over,
});
const episode = (n: number, over: Partial<CalendarEntry> = {}): CalendarEntry => ({
  kind: 'Episode', date: '2026-11-20', isRegionDate: false, seriesId: 5, episodeId: 100 + n, title: 'Show', year: 2020,
  seasonNumber: 2, episodeNumber: n, runtime: 45, monitored: true, hasFile: false, downloading: false, ...over,
});

describe('calendar ranges', () => {
  it('covers whole weeks in the month grid', () => {
    const { start, end } = viewRange('month', parseIsoDate('2026-11-20'), 1);
    expect(toIsoDate(start)).toBe('2026-10-26');
    expect(toIsoDate(end)).toBe('2026-12-06');
  });

  it('honours a Sunday week start', () => {
    const { start, end } = viewRange('week', parseIsoDate('2026-11-20'), 0);
    expect([toIsoDate(start), toIsoDate(end)]).toEqual(['2026-11-15', '2026-11-21']);
  });

  it('shows the calendar month in the agenda and moves by month', () => {
    const { start, end } = viewRange('agenda', parseIsoDate('2026-02-14'), 1);
    expect([toIsoDate(start), toIsoDate(end)]).toEqual(['2026-02-01', '2026-02-28']);
    expect(toIsoDate(shiftAnchor('agenda', parseIsoDate('2026-01-31'), 1))).toBe('2026-02-01');
    expect(toIsoDate(shiftAnchor('week', parseIsoDate('2026-01-31'), -1))).toBe('2026-01-24');
  });
});

describe('calendar items', () => {
  it('groups episodes of a series aired the same day', () => {
    const items = buildItems([episode(3), episode(1), episode(2, { hasFile: true }), movie()], DEFAULT_FILTERS);
    expect(items.map(i => [i.title, i.subtitle])).toEqual([['Show', 'S02E01–E03'], ['Film', 'Digital']]);
    expect(items[0].hasFile).toBe(false);
    expect(items[0].link).toEqual(['/series', 5]);
  });

  it('labels episode runs across seasons', () => {
    expect(episodeRangeLabel([episode(10), episode(1, { seasonNumber: 3 })])).toBe('S02E10–S03E01');
  });

  it('filters by media, release type, monitoring and status', () => {
    const entries = [
      movie({ releaseType: 'Theatrical' }), movie({ releaseType: 'Physical', hasFile: true }),
      movie({ movieId: 2, monitored: false }), episode(1),
    ];
    const keys = (f: Partial<typeof DEFAULT_FILTERS>) => buildItems(entries, { ...DEFAULT_FILTERS, ...f }).map(i => i.key);
    expect(keys({})).toEqual(['m-1-Theatrical', 'm-1-Physical', 's-5-2026-11-20']);
    expect(keys({ unmonitored: true })).toContain('m-2-Digital');
    expect(keys({ theatrical: false, series: false })).toEqual(['m-1-Physical']);
    expect(keys({ status: 'downloaded' })).toEqual(['m-1-Physical']);
    expect(keys({ status: 'missing', movies: false })).toEqual(['s-5-2026-11-20']);
    expect([...groupByDay(buildItems(entries, DEFAULT_FILTERS)).keys()]).toEqual(['2026-11-20']);
  });

  it('derives the display state', () => {
    const [cinema, digital] = buildItems([movie({ releaseType: 'Theatrical' }), movie()], DEFAULT_FILTERS);
    expect(itemState(cinema, '2026-11-21')).toBe('upcoming');
    expect(itemState(digital, '2026-11-21')).toBe('missing');
    expect(itemState(digital, '2026-11-19')).toBe('upcoming');
    expect(itemState({ ...digital, downloading: true }, '2026-11-21')).toBe('downloading');
    expect(itemState({ ...digital, monitored: false }, '2026-11-21')).toBe('unmonitored');
  });
});

describe('feed url', () => {
  it('builds an authenticated iCal url', () => {
    const url = new URL(feedUrl('https://media.example/krakarr/', 'k3y', {
      movies: true, series: false, theatrical: false, digital: true, physical: true, unmonitored: false, pastDays: 7, futureDays: 60,
    }));
    expect(url.pathname).toBe('/krakarr/api/v1/calendar/feed.ics');
    expect(Object.fromEntries(url.searchParams)).toEqual({
      apikey: 'k3y', pastDays: '7', futureDays: '60', movies: 'true', series: 'false', unmonitored: 'false', releaseTypes: 'digital,physical',
    });
  });

  it('limits the feed to one user when asked', () => {
    const url = new URL(feedUrl('https://media.example/', 'k3y', {
      movies: true, series: true, theatrical: true, digital: true, physical: true, unmonitored: false, pastDays: 7, futureDays: 60,
    }, 4));
    expect(url.searchParams.get('userId')).toBe('4');
    expect(url.searchParams.has('releaseTypes')).toBe(false);
  });
});
