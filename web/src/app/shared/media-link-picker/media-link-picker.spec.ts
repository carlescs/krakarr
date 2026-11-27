import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { MediaLink, MediaLinkPicker, filterLinks, linkFields, normalizeTitle, suggestLinks } from './media-link-picker';

const items: MediaLink[] = [
  { kind: 'series', id: 1, title: 'The Office', year: 2005 },
  { kind: 'series', id: 2, title: 'The Office (US)', year: 2005 },
  { kind: 'movie', id: 3, title: 'Dune: Part Two', year: 2024 },
  { kind: 'movie', id: 4, title: 'Dune', year: 2021 },
  { kind: 'movie', id: 5, title: 'Amélie', year: 2001 },
];

describe('media link picker helpers', () => {
  it('normalizes titles', () => {
    expect(normalizeTitle('Amélie (2001)')).toBe('amelie 2001');
    expect(normalizeTitle('Law & Order: SVU')).toBe('law and order svu');
  });

  it('filters by typed text, prefix matches first', () => {
    expect(filterLinks(items, 'dune').map(i => i.id)).toEqual([4, 3]);
    expect(filterLinks(items, 'part two').map(i => i.id)).toEqual([3]);
    expect(filterLinks(items, 'amelie').map(i => i.id)).toEqual([5]);
    expect(filterLinks(items, '  ')).toEqual([]);
  });

  it('suggests items from a release name, most specific first', () => {
    expect(suggestLinks(items, 'The.Office.US.S02E01.1080p.WEB-DL-GRP').map(i => i.id)).toEqual([2, 1]);
    expect(suggestLinks(items, 'Dune.Part.Two.2024.2160p.UHD.BluRay').map(i => i.id)).toEqual([3, 4]);
    expect(suggestLinks(items, 'Dune.2021.1080p.BluRay').map(i => i.id)).toEqual([4]);
    expect(suggestLinks(items, 'Dunes.of.Mars.2020')).toEqual([]);
  });

  it('maps a link to request fields', () => {
    expect(linkFields(null)).toEqual({});
    expect(linkFields(items[0])).toEqual({ seriesId: 1 });
    expect(linkFields(items[3])).toEqual({ movieId: 4 });
  });
});

describe('MediaLinkPicker', () => {
  it('suggests library items from the hint, filters as you type and picks one', async () => {
    setupTest({
      api: mockApi({
        movies: async () => ({ items: [{ id: 4, title: 'Dune', year: 2021 }], page: 1, pageSize: 200, total: 1, libraryTotal: 1 }),
        seriesList: async () => ({ items: [{ id: 1, title: 'The Office', year: 2005 }], page: 1, pageSize: 200, total: 1, libraryTotal: 1 }),
      }),
    });
    const fixture = TestBed.createComponent(MediaLinkPicker);
    fixture.componentRef.setInput('hint', 'Dune.2021.1080p.BluRay');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Suggestions');
    expect(text(el)).toContain('Dune (2021)');

    const input = el.querySelector('input') as HTMLInputElement;
    input.value = 'office';
    input.dispatchEvent(new Event('input'));
    await settle(fixture);
    expect(text(el)).not.toContain('Dune');
    button(el, 'The Office').click();
    await settle(fixture);
    expect(fixture.componentInstance.link()).toEqual({ kind: 'series', id: 1, title: 'The Office', year: 2005 });
    expect(text(el)).toContain('Imported into the library');

    button(el, 'Remove link').click();
    await settle(fixture);
    expect(fixture.componentInstance.link()).toBeNull();
  });

  it('still works when the library cannot be loaded', async () => {
    setupTest({
      api: mockApi({
        movies: async () => { throw new Error('x'); },
        seriesList: async () => ({ items: [], page: 1, pageSize: 200, total: 0, libraryTotal: 0 }),
      }),
    });
    const fixture = TestBed.createComponent(MediaLinkPicker);
    await settle(fixture);
    const input = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    expect(input.placeholder).toBe('Search your movies and series (optional)');
  });
});
