import { describe, expect, it } from 'vitest';
import { PersonCredit } from '../core/models';
import { age, creditItem, departments } from './person';

const credit = (c: Partial<PersonCredit>): PersonCredit => ({ kind: 'movie', tmdbId: 1, title: 'T', year: 2000, department: 'Acting', ...c });

describe('person page helpers', () => {
  it('lists the known-for department first, then by number of credits', () => {
    const credits = [
      credit({ department: 'Writing' }), credit({ department: 'Production' }), credit({ department: 'Production' }), credit({ department: 'Directing' }),
    ];
    expect(departments(credits, 'Directing')).toEqual(['Directing', 'Production', 'Writing']);
    expect(departments(credits)).toEqual(['Production', 'Directing', 'Writing']);
  });

  it('turns credits into discover items linked to the library', () => {
    expect(creditItem(credit({ tmdbId: 603, movieId: 7 }))).toEqual({
      kind: 'movie', item: { tmdbId: 603, title: 'T', year: 2000, overview: undefined, posterUrl: undefined, rating: undefined, runtime: 0, movieId: 7 },
    });
    const show = creditItem(credit({ kind: 'series', tmdbId: 1396, seriesId: 3 }));
    expect(show.kind).toBe('series');
    expect(show.kind === 'series' && show.item.seriesId).toBe(3);
  });

  it('computes ages in whole years', () => {
    expect(age('1964-09-02', new Date(2026, 8, 1))).toBe(61);
    expect(age('1964-09-02', new Date(2026, 8, 2))).toBe(62);
  });
});
