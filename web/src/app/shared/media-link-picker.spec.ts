import { describe, expect, it } from 'vitest';
import { MediaLink, filterLinks, linkFields, normalizeTitle, suggestLinks } from './media-link-picker';

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
