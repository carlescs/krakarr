import { describe, expect, it } from 'vitest';
import { interleave, movieItems, seriesItems } from './discover';
import { detailRoute, discoverKey, libraryRoute } from '../shared/media-card';

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

  it('links titles outside the library to their TMDB page', () => {
    const [owned, missing] = movieItems([
      { tmdbId: 603, title: 'The Matrix', year: 1999, runtime: 0, movieId: 7 },
      { tmdbId: 604, title: 'The Matrix Reloaded', year: 2003, runtime: 0 },
    ]);
    const [show, tvdbOnly] = seriesItems([{ tmdbId: 1396, title: 'Breaking Bad', year: 2008 }, { tvdbId: 81189, title: 'Breaking Bad', year: 2008 }]);
    expect(detailRoute(owned)).toEqual(['/movies', 7]);
    expect(detailRoute(missing)).toEqual(['/movies', 'tmdb', 604]);
    expect(detailRoute(show)).toEqual(['/series', 'tmdb', 1396]);
    expect(detailRoute(tvdbOnly)).toBeNull();
  });
});
