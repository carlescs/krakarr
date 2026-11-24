import { Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { debounceTime, merge } from 'rxjs';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import {
  CALENDAR_STATES as STATES, CALENDAR_STYLES as STYLES, CalendarFilters, CalendarItem, CalendarStatusFilter, CalendarView, DEFAULT_FILTERS, FeedOptions, RELEASE_LABELS,
  StyleKey, addDays, buildItems, daysBetween, feedUrl, groupByDay, itemState, localeFirstDayOfWeek, parseIsoDate, shiftAnchor, styleKey, toIsoDate, viewRange,
} from '../../core/calendar';
import { Events } from '../../core/events';
import { CalendarEntry, Subscriber } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal/modal';
import { Poster } from '../../shared/poster/poster';

const STORAGE_KEY = 'krakarr.calendar';

/** Whose titles the calendar shows: the whole library, the signed-in user's followed titles, or (staff) another user's. */
type Owner = 'all' | 'me' | number;

/**
 * `whose` replaces `owner`: every visit used to save the old default ("me"), which would otherwise pin staff
 * to their followed titles after the default changed.
 */
interface Stored { view?: CalendarView; filters?: Partial<CalendarFilters>; feed?: Partial<FeedOptions>; whose?: Owner; }

function readStored(): Stored {
  try { return JSON.parse(localStorage.getItem(STORAGE_KEY) ?? '{}') as Stored; } catch { return {}; }
}

@Component({
  selector: 'kr-calendar',
  imports: [FormsModule, RouterLink, NgTemplateOutlet, Modal, Poster],
  templateUrl: './calendar.html',
  styleUrl: './calendar.scss',
})
export class CalendarPage {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly styles = STYLES;
  protected readonly states = STATES;
  protected readonly maxChips = 4;
  protected readonly views: { id: CalendarView; label: string }[] = [
    { id: 'month', label: 'Month' }, { id: 'week', label: 'Week' }, { id: 'agenda', label: 'Agenda' },
  ];
  protected readonly statuses: { id: CalendarStatusFilter; label: string }[] = [
    { id: 'all', label: 'Any' }, { id: 'missing', label: 'Missing' }, { id: 'downloaded', label: 'Downloaded' },
  ];
  protected readonly releaseTypes = [
    { key: 'theatrical', style: 'Theatrical' }, { key: 'digital', style: 'Digital' }, { key: 'physical', style: 'Physical' },
  ] as const;

  private readonly stored = readStored();
  private readonly firstDay = localeFirstDayOfWeek();
  protected readonly today = toIsoDate(new Date());

  protected readonly view = signal<CalendarView>(
    this.stored.view ?? (typeof matchMedia !== 'undefined' && matchMedia('(max-width: 639px)').matches ? 'agenda' : 'month'));
  protected readonly anchor = signal(new Date());
  protected readonly filters = signal<CalendarFilters>({ ...DEFAULT_FILTERS, ...this.stored.filters });
  /**
   * Staff start on the whole library they manage; plain users on the titles they follow. Titles only get followed
   * when added or requested, so a library that predates following (or was imported) follows nothing yet.
   */
  protected readonly owner = signal<Owner>(this.stored.whose ?? (this.auth.isStaff() ? 'all' : 'me'));
  /** Staff: users who follow titles, to view the calendar as them. */
  protected readonly subscribers = signal<Subscriber[]>([]);
  protected readonly entries = signal<CalendarEntry[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedDay = signal<string | null>(null);
  protected readonly filtersOpen = signal(false);
  protected readonly feedOpen = signal(false);
  protected readonly apiKey = signal<string | null>(null);
  protected feed: FeedOptions = {
    movies: true, series: true, theatrical: true, digital: true, physical: true, unmonitored: false, pastDays: 14, futureDays: 90, ...this.stored.feed,
  };

  protected readonly range = computed(() => viewRange(this.view(), this.anchor(), this.firstDay));
  protected readonly days = computed(() => daysBetween(this.range().start, this.range().end));
  protected readonly items = computed(() => buildItems(this.entries(), this.filters()));
  protected readonly byDay = computed(() => groupByDay(this.items()));
  protected readonly agendaDays = computed(() => this.days().filter(d => this.byDay().has(d)));
  /** Nothing to show in the range (with the current filters), once loaded. */
  protected readonly empty = computed(() => !this.loading() && !this.error() && this.items().length === 0);
  protected readonly hiddenCount = computed(() => {
    const all = buildItems(this.entries(), { ...DEFAULT_FILTERS, unmonitored: true }).length;
    return Math.max(0, all - this.items().length);
  });
  /** Items per kind in the range, ignoring the kind toggles but honouring status and unmonitored. */
  protected readonly counts = computed(() => {
    const f = this.filters();
    const all = buildItems(this.entries(), { ...DEFAULT_FILTERS, unmonitored: f.unmonitored, status: f.status });
    const counts: Record<StyleKey | 'movies', number> = { episodes: 0, movies: 0, Theatrical: 0, Digital: 0, Physical: 0 };
    for (const item of all) {
      counts[this.styleKey(item)]++;
      if (item.kind === 'movie') counts.movies++;
    }
    return counts;
  });
  protected readonly activeFilterCount = computed(() => {
    const f = this.filters();
    return (Object.keys(DEFAULT_FILTERS) as (keyof CalendarFilters)[])
      .filter(k => f[k] !== DEFAULT_FILTERS[k] && !(f.movies === false && (k === 'theatrical' || k === 'digital' || k === 'physical')))
      .length;
  });
  /** Colours of the kinds currently shown, for the phone summary. */
  protected readonly activeKinds = computed(() => {
    const f = this.filters();
    const kinds: StyleKey[] = f.series ? ['episodes'] : [];
    if (f.movies) for (const t of this.releaseTypes) if (f[t.key]) kinds.push(t.style);
    return kinds;
  });
  protected readonly filterSummary = computed(() => {
    const f = this.filters();
    const types = this.releaseTypes.filter(t => f[t.key]);
    const movies = !f.movies || types.length === 0 ? null
      : types.length === this.releaseTypes.length ? 'Movies' : `Movies (${types.map(t => STYLES[t.style].label.toLowerCase()).join(', ')})`;
    const parts = [f.series ? 'Series' : null, movies].filter(Boolean);
    const what = parts.length ? parts.join(', ') : 'Nothing selected';
    const status = f.status === 'missing' ? ' · missing' : f.status === 'downloaded' ? ' · downloaded' : '';
    return what + status + (f.unmonitored ? ' · incl. unmonitored' : '');
  });
  protected readonly weekdays = computed(() => {
    const start = startOfDisplayedWeek(this.firstDay);
    return Array.from({ length: 7 }, (_, i) => {
      const d = addDays(start, i);
      return { short: d.toLocaleDateString(undefined, { weekday: 'short' }), narrow: d.toLocaleDateString(undefined, { weekday: 'narrow' }) };
    });
  });
  protected readonly periodName = computed(() => (this.view() === 'week' ? 'week' : 'month'));
  protected readonly rangeLabel = computed(() => {
    const { start, end } = this.range();
    if (this.view() !== 'week') return this.anchor().toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
    const fmt = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
    return fmt.formatRange ? fmt.formatRange(start, end) : `${fmt.format(start)} – ${fmt.format(end)}`;
  });

  private requestId = 0;
  private scrollToToday = true;

  constructor() {
    effect(() => { void this.load(this.range(), false, this.owner()); });
    if (this.auth.isStaff()) {
      this.api.subscribers().then(users => {
        this.subscribers.set(users);
        // A stored user who no longer follows anything (or was removed) falls back to your own titles.
        const owner = this.owner();
        if (typeof owner === 'number' && !users.some(u => u.id === owner)) this.owner.set('me');
      }).catch(() => { /* picker keeps Everything / My titles */ });
    }
    effect(() => this.persist());
    merge(this.events.movieUpdated, this.events.seriesUpdated, this.events.historyAdded)
      .pipe(debounceTime(1500), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => void this.load(this.range(), true, this.owner()));
  }

  private async load(range: { start: Date; end: Date }, quiet: boolean, owner: Owner) {
    const id = ++this.requestId;
    if (!quiet) this.loading.set(true);
    try {
      const entries = await this.api.calendar(toIsoDate(range.start), toIsoDate(range.end), owner === 'all' ? null : owner);
      if (id !== this.requestId) return;
      this.entries.set(entries);
      this.error.set(null);
      if (this.scrollToToday && this.view() === 'agenda') {
        this.scrollToToday = false;
        setTimeout(() => this.scrollToAgendaDay());
      }
    } catch (e) {
      if (id === this.requestId) this.error.set(errorMessage(e));
    } finally {
      if (id === this.requestId) this.loading.set(false);
    }
  }

  private scrollToAgendaDay() {
    const target = this.agendaDays().find(d => d >= this.today);
    if (target && target !== this.agendaDays()[0]) {
      document.getElementById('day-' + target)?.scrollIntoView({ block: 'start' });
    }
  }

  private persist() {
    const value: Stored = { view: this.view(), filters: this.filters(), feed: this.feed, whose: this.owner() };
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(value)); } catch { /* storage unavailable */ }
  }

  protected setView(view: CalendarView) {
    this.view.set(view);
    this.selectedDay.set(null);
    this.scrollToToday = view === 'agenda';
  }

  protected move(direction: 1 | -1) {
    this.anchor.set(shiftAnchor(this.view(), this.anchor(), direction));
    this.selectedDay.set(null);
  }

  protected goToday() {
    this.anchor.set(new Date());
    this.selectedDay.set(this.view() === 'month' ? this.today : null);
    this.scrollToToday = true;
    if (this.view() === 'agenda') setTimeout(() => this.scrollToAgendaDay());
  }

  protected selectDay(day: string) { this.selectedDay.set(this.selectedDay() === day ? null : day); }

  protected toggle(key: 'movies' | 'series' | 'theatrical' | 'digital' | 'physical' | 'unmonitored') {
    this.filters.update(f => ({ ...f, [key]: !f[key] }));
  }

  protected isOn(key: keyof CalendarFilters) { return this.filters()[key] === true; }

  protected resetFilters() { this.filters.set({ ...DEFAULT_FILTERS }); }

  protected setFilter<K extends keyof CalendarFilters>(key: K, value: CalendarFilters[K]) {
    this.filters.update(f => ({ ...f, [key]: value }));
  }

  protected chip(active: boolean) {
    return 'inline-flex shrink-0 items-center gap-1.5 rounded-full border px-3 py-1 text-xs font-medium transition pointer-coarse:min-h-9 '
      + (active ? 'border-surface-600 bg-surface-700 text-white' : 'border-surface-700 text-slate-500 hover:text-slate-300');
  }

  protected inMonth(day: string) { return parseIsoDate(day).getMonth() === this.anchor().getMonth(); }
  protected dayNumber(day: string) { return parseIsoDate(day).getDate(); }
  protected weekdayName(day: string) { return parseIsoDate(day).toLocaleDateString(undefined, { weekday: 'short' }); }
  protected longDate(day: string) {
    return parseIsoDate(day).toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
  }
  private shortDate(day: string) { return parseIsoDate(day).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }); }

  protected styleKey(item: CalendarItem): StyleKey { return styleKey(item); }
  protected state(item: CalendarItem) { return itemState(item, this.today); }
  protected stateText(item: CalendarItem) {
    const s = this.state(item);
    return s === 'downloaded' ? 'text-emerald-300' : s === 'downloading' ? 'text-accent-300' : 'text-rose-300';
  }

  /** Secondary line: episode title(s) and network, or where a movie date comes from. */
  protected detail(item: CalendarItem): string {
    const e = item.entries[0];
    if (item.kind === 'episodes') {
      const what = item.entries.length === 1 ? (e.episodeTitle || 'TBA') : `${item.entries.length} episodes`;
      return e.network ? `${what} · ${e.network}` : what;
    }
    const where = e.country ? (e.isRegionDate ? e.country : `${e.country}, earliest`) : '';
    const earlier = e.earliestDate ? ` · ${e.earliestCountry} ${this.shortDate(e.earliestDate)}` : '';
    return [e.year ? String(e.year) : '', where].filter(Boolean).join(' · ') + earlier;
  }

  protected tooltip(item: CalendarItem) {
    return `${item.title} — ${item.subtitle}\n${this.detail(item)}\n${STATES[this.state(item)].label}`;
  }

  // ---- iCal ----
  async openFeed() {
    this.feedOpen.set(true);
    if (!this.apiKey()) {
      try { this.apiKey.set((await this.api.settings('general')).apiKey); } catch (e) { this.toasts.error(errorMessage(e)); }
    }
  }

  protected feedLink(key: string) {
    const owner = this.owner();
    return feedUrl(document.baseURI, key, this.feed, typeof owner === 'number' ? owner : undefined);
  }

  protected closeFeed() {
    this.feedOpen.set(false);
    this.persist();
  }

  protected async copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      this.toasts.success('Feed URL copied');
    } catch {
      this.toasts.error('Copy failed — select the URL and copy it manually');
    }
  }
}

function startOfDisplayedWeek(firstDay: number): Date {
  const d = new Date(2024, 0, 7); // a Sunday
  return addDays(d, firstDay);
}
