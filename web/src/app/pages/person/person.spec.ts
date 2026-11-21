import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { PersonCredit, PersonDetails } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { PersonPage, age, creditItem, departments, sortCredits } from './person';

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

  it('sorts credits by date, title or rating', () => {
    const credits = [
      credit({ tmdbId: 1, title: 'b', date: '2001-05-01', rating: 6 }),
      credit({ tmdbId: 2, title: 'A', date: '2010-01-01', rating: 8 }),
      credit({ tmdbId: 3, title: 'c', year: 0 }),
      credit({ tmdbId: 4, title: 'd', date: '1995-03-03', rating: 8 }),
    ];
    const ids = (order: Parameters<typeof sortCredits>[1]) => sortCredits(credits, order).map(c => c.tmdbId);
    expect(ids('newest')).toEqual([3, 2, 1, 4]);
    expect(ids('oldest')).toEqual([4, 1, 2, 3]);
    expect(ids('title')).toEqual([2, 1, 3, 4]);
    expect(ids('rating')).toEqual([2, 4, 1, 3]);
    expect(credits.map(c => c.tmdbId)).toEqual([1, 2, 3, 4]);
  });
});

describe('PersonPage', () => {
  afterEach(() => localStorage.clear());

  const person: PersonDetails = {
    tmdbId: 1, name: 'Denis Villeneuve', biography: 'Canadian filmmaker.', birthday: '1967-10-03', knownForDepartment: 'Directing',
    credits: [
      credit({ tmdbId: 10, title: 'Arrival', year: 2016, department: 'Directing', movieId: 3 }),
      credit({ tmdbId: 11, title: 'Dune', year: 2021, department: 'Directing' }),
      credit({ tmdbId: 12, title: 'Dune', year: 2021, department: 'Writing' }),
      credit({ kind: 'series', tmdbId: 13, title: 'Dune: Prophecy', year: 2024, department: 'Directing' }),
    ],
  };

  async function render() {
    const api = setupTest({ api: mockApi({ person: async () => person }) });
    const fixture = TestBed.createComponent(PersonPage);
    fixture.componentRef.setInput('id', '1');
    await settle(fixture);
    return { api, fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('shows the person with credits of their main department', async () => {
    const { api, el } = await render();
    expect(api.person).toHaveBeenCalledWith(1);
    expect(text(el, 'h1')).toBe('Denis Villeneuve');
    expect(text(el)).toContain('Canadian filmmaker.');
    expect([...el.querySelectorAll('h2')].map(h => text(h as HTMLElement))).toEqual(['Movies 2', 'Series 1']);
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
  });

  it('filters by department and media type', async () => {
    const { fixture, el } = await render();
    button(el, 'Writing').click();
    await settle(fixture);
    expect([...el.querySelectorAll('h2')].map(h => text(h as HTMLElement))).toEqual(['Movies 1']);

    button(el, 'Directing').click();
    type(el, 'select[aria-label="Media type"]', 'series');
    await settle(fixture);
    expect([...el.querySelectorAll('h2')].map(h => text(h as HTMLElement))).toEqual(['Series 1']);
  });

  it('shows errors', async () => {
    setupTest({ api: mockApi({ person: async () => { throw new Error('Person not found'); } }) });
    const fixture = TestBed.createComponent(PersonPage);
    fixture.componentRef.setInput('id', 5);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Person not found');
  });
});
