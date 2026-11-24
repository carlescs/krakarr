import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { addDays, toIsoDate } from '../../../core/calendar';
import { CalendarEntry } from '../../../core/models';
import { mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { UPCOMING_DAYS, UPCOMING_LIMIT, Upcoming } from './upcoming';

const day = (offset: number) => toIsoDate(addDays(new Date(), offset));

function episode(id: number, date: string, overrides: Partial<CalendarEntry> = {}): CalendarEntry {
  return {
    kind: 'Episode', date, isRegionDate: false, seriesId: 7, episodeId: id, title: 'Severance', year: 2022, seasonNumber: 2, episodeNumber: id,
    episodeTitle: `Episode ${id}`, network: 'Apple TV+', posterUrl: '/poster.jpg', runtime: 50, monitored: true, hasFile: false, downloading: false, ...overrides,
  };
}

function movie(id: number, date: string, overrides: Partial<CalendarEntry> = {}): CalendarEntry {
  return {
    kind: 'Movie', date, releaseType: 'Digital', isRegionDate: true, movieId: id, title: `Movie ${id}`, year: 2026, runtime: 120,
    monitored: true, hasFile: false, downloading: false, ...overrides,
  };
}

describe('Upcoming', () => {
  it('asks for the next two weeks and lists releases by day with posters', async () => {
    const api = setupTest({
      api: mockApi({
        calendar: async () => [
          episode(1, day(0)), episode(2, day(0)),
          movie(3, day(1), { downloading: true }),
          movie(4, day(3), { monitored: false }),
        ],
      }),
    });
    const fixture = TestBed.createComponent(Upcoming);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(api.calendar).toHaveBeenCalledWith(day(0), day(UPCOMING_DAYS - 1));
    const days = el.querySelectorAll('[data-day]');
    expect(days.length).toBe(2); // the unmonitored movie is left out
    expect(text(days[0] as HTMLElement, '[data-day-name]')).toBe('Today');
    // Episodes of a series on the same day are grouped.
    expect(text(days[0] as HTMLElement)).toContain('S02E01–E02');
    expect(text(days[0] as HTMLElement)).toContain('2 episodes · Apple TV+');
    expect(days[0].querySelector('a[href="/series/7"] kr-poster')).not.toBeNull();
    expect(text(days[1] as HTMLElement, '[data-day-name]')).toBe('Tomorrow');
    expect(text(days[1] as HTMLElement)).toContain('Movie 3');
    expect(text(days[1] as HTMLElement)).toContain('Downloading');
  });

  it('caps the list and links to the calendar for the rest', async () => {
    setupTest({ api: mockApi({ calendar: async () => Array.from({ length: UPCOMING_LIMIT + 3 }, (_, i) => movie(i + 1, day(i % UPCOMING_DAYS))) }) });
    const fixture = TestBed.createComponent(Upcoming);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('[data-list="upcoming"] a').length).toBe(UPCOMING_LIMIT);
    expect(text(el, '[data-section="upcoming"]')).toContain('and 3 more on the calendar');
  });

  it('says when nothing is coming up', async () => {
    setupTest({ api: mockApi({ calendar: async () => [] }) });
    const fixture = TestBed.createComponent(Upcoming);
    await settle(fixture);
    expect(text(fixture.nativeElement, '[data-section="upcoming"]')).toContain('Nothing monitored airs or releases in the next two weeks.');
  });
});
