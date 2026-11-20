import { MediaRequest } from './models';

/** Short label and badge colours for a request's state, combining its status with how much of it is downloaded. */
export function requestBadge(r: MediaRequest): { label: string; classes: string } {
  if (r.status === 'Declined') return { label: 'Declined', classes: 'bg-rose-500/15 text-rose-300' };
  if (r.status === 'Pending') return { label: 'Pending', classes: 'bg-amber-500/15 text-amber-300' };
  switch (r.availability) {
    case 'Available': return { label: 'Available', classes: 'bg-emerald-500/15 text-emerald-300' };
    case 'Partial': return { label: 'Partially available', classes: 'bg-sky-500/15 text-sky-300' };
    case 'Missing': return { label: 'Approved · downloading', classes: 'bg-accent-500/15 text-accent-300' };
    default: return { label: 'Approved · removed from library', classes: 'bg-slate-500/15 text-slate-400' };
  }
}

/** Where a request's title opens: its library page once added, otherwise its TMDB detail page. */
export function requestRoute(r: MediaRequest): (string | number)[] {
  const base = r.mediaType === 'Movie' ? '/movies' : '/series';
  return r.libraryId ? [base, r.libraryId] : [base, 'tmdb', r.tmdbId];
}

/** Staff may remove any request; users (whose list only holds their own) may withdraw pending ones. */
export function canDeleteRequest(r: MediaRequest, staff: boolean): boolean {
  return staff || r.status === 'Pending';
}
