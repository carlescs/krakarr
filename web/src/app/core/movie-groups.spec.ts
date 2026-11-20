import { describe, expect, it } from 'vitest';
import { MovieCollection, MovieListItem } from './models';
import { groupByCollection } from './movie-groups';

const movie = (id: number, title: string, year: number, collectionId: number | null = null, hasFile = false) =>
  ({ id, title, year, collectionId, hasFile }) as MovieListItem;

describe('groupByCollection', () => {
  const collections: MovieCollection[] = [
    { id: 2, tmdbId: 119, name: 'The Lord of the Rings Collection' },
    { id: 1, tmdbId: 2344, name: 'The Matrix Collection' },
    { id: 3, tmdbId: 9, name: 'Unused Collection' },
  ];

  it('orders collections as given, movies by year, and puts the rest last', () => {
    const groups = groupByCollection([
      movie(1, 'Alien', 1979),
      movie(2, 'The Matrix Reloaded', 2003, 1, true),
      movie(3, 'The Return of the King', 2003, 2),
      movie(4, 'The Matrix', 1999, 1, true),
      movie(5, 'The Fellowship of the Ring', 2001, 2, true),
      movie(6, 'Orphan', 2009, 99), // collection not in the list yet
    ], collections);

    expect(groups.map(g => g.title)).toEqual(['The Lord of the Rings Collection', 'The Matrix Collection', 'Other movies']);
    expect(groups[0].movies.map(m => m.id)).toEqual([5, 3]);
    expect(groups[1].movies.map(m => m.id)).toEqual([4, 2]);
    expect(groups[2].movies.map(m => m.id)).toEqual([1, 6]);
    expect(groups.map(g => g.downloaded)).toEqual([1, 2, 0]);
    expect(groups[2].collection).toBeNull();
  });

  it('leaves out empty groups', () => {
    expect(groupByCollection([], collections)).toEqual([]);
    expect(groupByCollection([movie(1, 'The Matrix', 1999, 1)], collections).map(g => g.key)).toEqual(['c1']);
  });
});
