import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { debounceTime } from 'rxjs';
import { Api, errorMessage } from '../../../core/api';
import {
  CALENDAR_STATES, CALENDAR_STYLES, CalendarItem, DEFAULT_FILTERS, addDays, buildItems, itemState, parseIsoDate, styleKey, toIsoDate,
} from '../../../core/calendar';
import { CalendarEntry } from '../../../core/models';
import { Events } from '../../../core/events';
import { Poster } from '../../../shared/poster/poster';

/** How many days ahead the agenda looks, today included. */
export const UPCOMING_DAYS = 14;
/** Most releases listed; the rest are on the calendar. */
export const UPCOMING_LIMIT = 12;

export interface UpcomingDay { date: string; items: CalendarItem[]; }

/** Dashboard agenda of the monitored episodes and movie releases coming up in the next two weeks, grouped by day. */
@Component({
  selector: 'kr-upcoming',
  imports: [RouterLink, Poster],
  templateUrl: './upcoming.html',
  styleUrl: './upcoming.scss',
  host: { class: 'block' },
})
export class Upcoming implements OnInit {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly styles = CALENDAR_STYLES;
  protected readonly states = CALENDAR_STATES;
  protected readonly entries = signal<CalendarEntry[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected today = toIsoDate(new Date());

  /** Monitored releases, grouped per series and day like the calendar. */
  private readonly items = computed(() => buildItems(this.entries() ?? [], DEFAULT_FILTERS));
  protected readonly hidden = computed(() => Math.max(0, this.items().length - UPCOMING_LIMIT));
  protected readonly days = computed<UpcomingDay[]>(() => {
    const days: UpcomingDay[] = [];
    for (const item of this.items().slice(0, UPCOMING_LIMIT)) {
      const last = days[days.length - 1];
      if (last?.date === item.date) last.items.push(item); else days.push({ date: item.date, items: [item] });
    }
    return days;
  });

  ngOnInit() {
    this.load();
    // Grabs and imports change an item's state; they come in bursts.
    this.events.historyAdded
      .pipe(debounceTime(3000), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
  }

  private async load() {
    const now = new Date();
    this.today = toIsoDate(now);
    try {
      this.entries.set(await this.api.calendar(this.today, toIsoDate(addDays(now, UPCOMING_DAYS - 1))));
      this.error.set(null);
    } catch (err) {
      this.error.set(errorMessage(err));
    }
  }

  protected styleKey(item: CalendarItem) { return styleKey(item); }
  protected state(item: CalendarItem) { return itemState(item, this.today); }

  /** "Today", "Tomorrow" or the weekday. */
  protected dayName(date: string): string {
    if (date === this.today) return 'Today';
    if (date === toIsoDate(addDays(parseIsoDate(this.today), 1))) return 'Tomorrow';
    return parseIsoDate(date).toLocaleDateString(undefined, { weekday: 'long' });
  }

  protected dayNumber(date: string): number { return parseIsoDate(date).getDate(); }
  protected month(date: string): string { return parseIsoDate(date).toLocaleDateString(undefined, { month: 'short' }); }

  /** Episode title(s) and network, or the movie's year. */
  protected detail(item: CalendarItem): string {
    const e = item.entries[0];
    if (item.kind === 'movie') return e.year ? String(e.year) : '';
    const what = item.entries.length === 1 ? (e.episodeTitle || 'TBA') : `${item.entries.length} episodes`;
    return e.network ? `${what} · ${e.network}` : what;
  }
}
