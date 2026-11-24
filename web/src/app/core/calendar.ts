import { CalendarEntry, MinimumAvailability, ReleaseDateType } from './models';

export type CalendarView = 'month' | 'week' | 'agenda';
export type CalendarStatusFilter = 'all' | 'missing' | 'downloaded';

export interface CalendarFilters {
  movies: boolean;
  series: boolean;
  theatrical: boolean;
  digital: boolean;
  physical: boolean;
  unmonitored: boolean;
  status: CalendarStatusFilter;
}

export const DEFAULT_FILTERS: CalendarFilters = {
  movies: true, series: true, theatrical: true, digital: true, physical: true, unmonitored: false, status: 'all',
};

/** What the calendar shows for one day: a movie release, or one or more episodes of a series aired the same day. */
export interface CalendarItem {
  key: string;
  date: string;
  kind: 'movie' | 'episodes';
  entries: CalendarEntry[];
  /** Theatrical / Digital / Physical for movies. */
  releaseType?: ReleaseDateType;
  title: string;
  /** "In cinemas", "S01E03", "S02E01–E08" ... */
  subtitle: string;
  posterUrl?: string | null;
  link: (string | number)[];
  monitored: boolean;
  hasFile: boolean;
  downloading: boolean;
}

/** Choices for when automatic movie searches start. */
export const AVAILABILITY_OPTIONS: { value: MinimumAvailability; label: string }[] = [
  { value: 'Announced', label: 'Announced' },
  { value: 'InCinemas', label: 'In cinemas' },
  { value: 'Released', label: 'Released (digital or physical)' },
];

export type ItemState = 'downloaded' | 'downloading' | 'missing' | 'upcoming' | 'unmonitored';

export const RELEASE_LABELS: Record<ReleaseDateType, string> = { Theatrical: 'In cinemas', Digital: 'Digital', Physical: 'Physical' };

export type StyleKey = 'episodes' | 'Theatrical' | 'Digital' | 'Physical';

/** Colour per kind of release: left bar, chip background and legend dot. */
export const CALENDAR_STYLES: Record<StyleKey, { bar: string; chip: string; dot: string; label: string }> = {
  episodes: { bar: 'border-l-accent-400', chip: 'bg-accent-500/10 hover:bg-accent-500/20', dot: 'bg-accent-400', label: 'Episodes' },
  Theatrical: { bar: 'border-l-amber-400', chip: 'bg-amber-500/10 hover:bg-amber-500/20', dot: 'bg-amber-400', label: RELEASE_LABELS.Theatrical },
  Digital: { bar: 'border-l-violet-400', chip: 'bg-violet-500/10 hover:bg-violet-500/20', dot: 'bg-violet-400', label: RELEASE_LABELS.Digital },
  Physical: { bar: 'border-l-pink-400', chip: 'bg-pink-500/10 hover:bg-pink-500/20', dot: 'bg-pink-400', label: RELEASE_LABELS.Physical },
};

export const CALENDAR_STATES: Record<ItemState, { label: string; badge: string; icon: string }> = {
  downloaded: { label: 'Downloaded', badge: 'bg-emerald-500/15 text-emerald-300', icon: '✓' },
  downloading: { label: 'Downloading', badge: 'bg-accent-500/15 text-accent-300', icon: '↓' },
  missing: { label: 'Missing', badge: 'bg-rose-500/15 text-rose-300', icon: '!' },
  upcoming: { label: 'Upcoming', badge: 'bg-surface-700 text-slate-300', icon: '' },
  unmonitored: { label: 'Unmonitored', badge: 'bg-slate-500/15 text-slate-400', icon: '' },
};

export function styleKey(item: CalendarItem): StyleKey { return item.kind === 'episodes' ? 'episodes' : item.releaseType ?? 'Theatrical'; }

// ---- Dates (local, yyyy-MM-dd) -------------------------------------------------------------

export function toIsoDate(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

export function parseIsoDate(iso: string): Date {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
}

export function addDays(d: Date, days: number): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate() + days);
}

/** First day of the week for the user's locale (0 = Sunday, 1 = Monday ...); Monday when the browser cannot tell. */
export function localeFirstDayOfWeek(locale = typeof navigator !== 'undefined' ? navigator.language : 'en-GB'): number {
  try {
    const l = new Intl.Locale(locale) as Intl.Locale & { getWeekInfo?: () => { firstDay: number }; weekInfo?: { firstDay: number } };
    const firstDay = l.getWeekInfo?.().firstDay ?? l.weekInfo?.firstDay;
    if (firstDay) return firstDay % 7; // Intl uses 1-7 with 7 = Sunday
  } catch { /* unsupported */ }
  return 1;
}

export function startOfWeek(d: Date, firstDay: number): Date {
  return addDays(d, -((d.getDay() - firstDay + 7) % 7));
}

/** Inclusive date range a view shows around the anchor date. The month grid covers whole weeks. */
export function viewRange(view: CalendarView, anchor: Date, firstDay: number): { start: Date; end: Date } {
  if (view === 'week') {
    const start = startOfWeek(anchor, firstDay);
    return { start, end: addDays(start, 6) };
  }
  const first = new Date(anchor.getFullYear(), anchor.getMonth(), 1);
  const last = new Date(anchor.getFullYear(), anchor.getMonth() + 1, 0);
  if (view === 'agenda') return { start: first, end: last };
  const start = startOfWeek(first, firstDay);
  return { start, end: addDays(startOfWeek(last, firstDay), 6) };
}

/** Moves the anchor one period (month or week) forwards or backwards. */
export function shiftAnchor(view: CalendarView, anchor: Date, direction: 1 | -1): Date {
  if (view === 'week') return addDays(anchor, 7 * direction);
  return new Date(anchor.getFullYear(), anchor.getMonth() + direction, 1);
}

export function daysBetween(start: Date, end: Date): string[] {
  const days: string[] = [];
  for (let d = start; d <= end; d = addDays(d, 1)) days.push(toIsoDate(d));
  return days;
}

// ---- Entries -> items ----------------------------------------------------------------------

export function episodeCode(season?: number | null, episode?: number | null): string {
  const pad = (n?: number | null) => String(n ?? 0).padStart(2, '0');
  return `S${pad(season)}E${pad(episode)}`;
}

/** "S01E03", or "S01E01–E08" for a run of episodes of one season. */
export function episodeRangeLabel(entries: CalendarEntry[]): string {
  const sorted = [...entries].sort((a, b) => (a.seasonNumber ?? 0) - (b.seasonNumber ?? 0) || (a.episodeNumber ?? 0) - (b.episodeNumber ?? 0));
  const first = sorted[0], last = sorted[sorted.length - 1];
  if (sorted.length === 1) return episodeCode(first.seasonNumber, first.episodeNumber);
  if (first.seasonNumber === last.seasonNumber) {
    return `${episodeCode(first.seasonNumber, first.episodeNumber)}–E${String(last.episodeNumber ?? 0).padStart(2, '0')}`;
  }
  return `${episodeCode(first.seasonNumber, first.episodeNumber)}–${episodeCode(last.seasonNumber, last.episodeNumber)}`;
}

export function matchesFilters(e: CalendarEntry, f: CalendarFilters): boolean {
  if (e.kind === 'Movie') {
    if (!f.movies) return false;
    if (e.releaseType === 'Theatrical' && !f.theatrical) return false;
    if (e.releaseType === 'Digital' && !f.digital) return false;
    if (e.releaseType === 'Physical' && !f.physical) return false;
  } else if (!f.series) {
    return false;
  }
  if (!f.unmonitored && !e.monitored) return false;
  if (f.status === 'downloaded' && !e.hasFile) return false;
  if (f.status === 'missing' && e.hasFile) return false;
  return true;
}

/** Filters entries and groups episodes of the same series aired on the same day into one item. */
export function buildItems(entries: CalendarEntry[], filters: CalendarFilters): CalendarItem[] {
  const items: CalendarItem[] = [];
  const episodeGroups = new Map<string, CalendarItem>();
  for (const e of entries) {
    if (!matchesFilters(e, filters)) continue;
    if (e.kind === 'Movie') {
      items.push({
        key: `m-${e.movieId}-${e.releaseType}`, date: e.date, kind: 'movie', entries: [e], releaseType: e.releaseType ?? undefined,
        title: e.title, subtitle: RELEASE_LABELS[e.releaseType ?? 'Theatrical'], posterUrl: e.posterUrl,
        link: ['/movies', e.movieId!], monitored: e.monitored, hasFile: e.hasFile, downloading: e.downloading,
      });
      continue;
    }
    const key = `s-${e.seriesId}-${e.date}`;
    const group = episodeGroups.get(key);
    if (group) {
      group.entries.push(e);
      group.subtitle = episodeRangeLabel(group.entries);
      group.monitored ||= e.monitored;
      group.hasFile &&= e.hasFile;
      group.downloading ||= e.downloading;
    } else {
      const item: CalendarItem = {
        key, date: e.date, kind: 'episodes', entries: [e], title: e.title, subtitle: episodeRangeLabel([e]), posterUrl: e.posterUrl,
        link: ['/series', e.seriesId!], monitored: e.monitored, hasFile: e.hasFile, downloading: e.downloading,
      };
      episodeGroups.set(key, item);
      items.push(item);
    }
  }
  return items;
}

export function groupByDay(items: CalendarItem[]): Map<string, CalendarItem[]> {
  const map = new Map<string, CalendarItem[]>();
  for (const item of items) {
    const list = map.get(item.date);
    if (list) list.push(item); else map.set(item.date, [item]);
  }
  return map;
}

export function itemState(item: CalendarItem, today: string): ItemState {
  if (item.hasFile) return 'downloaded';
  if (item.downloading) return 'downloading';
  if (!item.monitored) return 'unmonitored';
  // A cinema release is not downloadable, so it is never "missing".
  if (item.date <= today && item.releaseType !== 'Theatrical') return 'missing';
  return 'upcoming';
}

// ---- iCal feed -----------------------------------------------------------------------------

export interface FeedOptions {
  movies: boolean; series: boolean; theatrical: boolean; digital: boolean; physical: boolean;
  unmonitored: boolean; pastDays: number; futureDays: number;
}

/** Absolute URL of the iCal feed, authenticated with the API key; `userId` limits it to the titles that user follows. */
export function feedUrl(base: string, apiKey: string, o: FeedOptions, userId?: number): string {
  const types = (['theatrical', 'digital', 'physical'] as const).filter(t => o[t]);
  const params = new URLSearchParams({
    apikey: apiKey,
    pastDays: String(o.pastDays),
    futureDays: String(o.futureDays),
    movies: String(o.movies && types.length > 0),
    series: String(o.series),
    unmonitored: String(o.unmonitored),
  });
  if (types.length > 0 && types.length < 3) params.set('releaseTypes', types.join(','));
  if (userId != null) params.set('userId', String(userId));
  return `${base.replace(/\/$/, '')}/api/v1/calendar/feed.ics?${params}`;
}
