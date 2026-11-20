import { MovieCollection, MovieListItem } from './models';

/** A section of the movie list: one TMDB collection, or the movies that belong to none. */
export interface MovieGroup {
  key: string;
  title: string;
  collection: MovieCollection | null;
  movies: MovieListItem[];
  downloaded: number;
}

export const OTHER_MOVIES_KEY = 'other';

/**
 * Groups movies by collection. Collections keep the order they are given in (sorted by name by the API) and their
 * movies are ordered by year, so a franchise reads in release order. Movies outside any collection (or in one the
 * collection list doesn't know yet) come last as "Other movies", in their original order. Empty groups are left out.
 */
export function groupByCollection(movies: readonly MovieListItem[], collections: readonly MovieCollection[]): MovieGroup[] {
  const byId = new Map<number, MovieListItem[]>();
  const other: MovieListItem[] = [];
  const known = new Set(collections.map(c => c.id));
  for (const m of movies) {
    if (m.collectionId != null && known.has(m.collectionId)) {
      const list = byId.get(m.collectionId);
      if (list) list.push(m); else byId.set(m.collectionId, [m]);
    } else {
      other.push(m);
    }
  }

  const groups: MovieGroup[] = [];
  for (const c of collections) {
    const list = byId.get(c.id);
    if (!list) continue;
    list.sort((a, b) => (a.year || 9999) - (b.year || 9999) || a.title.localeCompare(b.title));
    groups.push(group(`c${c.id}`, c.name, c, list));
  }
  if (other.length) groups.push(group(OTHER_MOVIES_KEY, 'Other movies', null, other));
  return groups;
}

function group(key: string, title: string, collection: MovieCollection | null, movies: MovieListItem[]): MovieGroup {
  return { key, title, collection, movies, downloaded: movies.filter(m => m.hasFile).length };
}
