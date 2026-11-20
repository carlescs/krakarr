import { describe, expect, it } from 'vitest';
import { interleave, movieItems, seriesItems } from './discover';
import { discoverKey, libraryRoute } from '../shared/media-card';

describe('discover helpers', () => {
  it('interleaves lists of different lengths', () => {
    expect(interleave<number | string>([1, 2, 3], ['a'])).toEqual([1, 'a', 2, 3]);
  });

  it('keys and links discovered items', () => {
    const [movie] = movieItems([{ tmdbId: 603, title: 'The Matrix', year: 1999, runtime: 0, movieId: 7 }]);
    const [show] = seriesItems([{ tvdbId: 81189, title: 'Breaking Bad', year: 2008 }]);
    expect(discoverKey(movie)).toBe('movie-603');
    expect(discoverKey(show)).toBe('series-tvdb81189');
    expect(libraryRoute(movie)).toEqual(['/movies', 7]);
    expect(libraryRoute(show)).toBeNull();
  });
});
