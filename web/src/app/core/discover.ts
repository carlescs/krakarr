import { DiscoverItem, MovieDiscoverList, MovieLookup, SeriesDiscoverList, SeriesLookup } from './models';

export const movieItems = (list: MovieLookup[]): DiscoverItem[] => list.map(item => ({ kind: 'movie', item }));
export const seriesItems = (list: SeriesLookup[]): DiscoverItem[] => list.map(item => ({ kind: 'series', item }));

export interface DiscoverListInfo<T extends string> { id: T; title: string; subtitle: string; }

export const MOVIE_LISTS: DiscoverListInfo<MovieDiscoverList>[] = [
  { id: 'trending', title: 'Trending movies', subtitle: 'This week on TMDB' },
  { id: 'popular', title: 'Popular movies', subtitle: 'Most watched right now' },
  { id: 'upcoming', title: 'Upcoming movies', subtitle: 'Releasing soon' },
  { id: 'now-playing', title: 'In theaters', subtitle: 'Now playing' },
  { id: 'top-rated', title: 'Top rated movies', subtitle: 'All-time favourites' },
];

export const SERIES_LISTS: DiscoverListInfo<SeriesDiscoverList>[] = [
  { id: 'trending', title: 'Trending series', subtitle: 'This week on TMDB' },
  { id: 'popular', title: 'Popular series', subtitle: 'Most watched right now' },
  { id: 'upcoming', title: 'Upcoming series', subtitle: 'Premiering soon' },
  { id: 'airing-today', title: 'Airing today', subtitle: 'New episodes today' },
  { id: 'top-rated', title: 'Top rated series', subtitle: 'All-time favourites' },
];

/** Mixes two lists by alternating their items, skipping gaps. */
export function interleave<T>(a: T[], b: T[]): T[] {
  const out: T[] = [];
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    if (i < a.length) out.push(a[i]);
    if (i < b.length) out.push(b[i]);
  }
  return out;
}
