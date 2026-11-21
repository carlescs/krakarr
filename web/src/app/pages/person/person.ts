import { Component, computed, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Title } from '@angular/platform-browser';
import { Api, errorMessage } from '../../core/api';
import { DiscoverItem, PersonCredit, PersonDetails } from '../../core/models';
import { DiscoverAdd } from '../../shared/discover-add/discover-add';
import { MediaCard, discoverKey } from '../../shared/media-card/media-card';

type MediaFilter = 'all' | 'movie' | 'series';
export type CreditOrder = 'newest' | 'oldest' | 'title' | 'rating';

const ORDER_KEY = 'krakarr.person.order';
const ORDERS: CreditOrder[] = ['newest', 'oldest', 'title', 'rating'];

function storedOrder(): CreditOrder {
  try {
    const v = localStorage.getItem(ORDER_KEY) as CreditOrder | null;
    return v && ORDERS.includes(v) ? v : 'newest';
  } catch { return 'newest'; }
}

/** Release date as a sortable string; titles without one are announced/upcoming, so they count as newest. */
const dateKey = (c: PersonCredit) => c.date ?? (c.year ? `${c.year}` : '9999');

/** Credits in the chosen order (a new array; ties keep the server order). */
export function sortCredits(credits: PersonCredit[], order: CreditOrder): PersonCredit[] {
  const compare: Record<CreditOrder, (a: PersonCredit, b: PersonCredit) => number> = {
    newest: (a, b) => dateKey(b).localeCompare(dateKey(a)),
    oldest: (a, b) => dateKey(a).localeCompare(dateKey(b)),
    title: (a, b) => a.title.localeCompare(b.title, undefined, { sensitivity: 'base', numeric: true }),
    rating: (a, b) => (b.rating ?? 0) - (a.rating ?? 0) || dateKey(b).localeCompare(dateKey(a)),
  };
  return [...credits].sort(compare[order]);
}

/** A credit as a discover item, so it renders with the shared poster card and add dialog. */
export function creditItem(c: PersonCredit): DiscoverItem {
  const base = { tmdbId: c.tmdbId, title: c.title, year: c.year, overview: c.overview, posterUrl: c.posterUrl, rating: c.rating };
  return c.kind === 'movie'
    ? { kind: 'movie', item: { ...base, runtime: 0, movieId: c.movieId } }
    : { kind: 'series', item: { ...base, seriesId: c.seriesId } };
}

/** Departments a person worked in, the one they are known for first, then by number of credits. */
export function departments(credits: PersonCredit[], knownFor?: string): string[] {
  const counts = new Map<string, number>();
  for (const c of credits) counts.set(c.department, (counts.get(c.department) ?? 0) + 1);
  return [...counts.keys()].sort((a, b) => (a === knownFor ? -1 : b === knownFor ? 1 : (counts.get(b)! - counts.get(a)!) || a.localeCompare(b)));
}

/** Age in whole years at a date (today, or the day they died). */
export function age(birthday: string, until: Date = new Date()): number {
  const born = new Date(birthday + 'T00:00:00');
  let years = until.getFullYear() - born.getFullYear();
  if (until.getMonth() < born.getMonth() || (until.getMonth() === born.getMonth() && until.getDate() < born.getDate())) years--;
  return years;
}

/** An actor, director or other crew member with their movies and shows. */
@Component({
  selector: 'kr-person',
  imports: [DatePipe, MediaCard, DiscoverAdd],
  templateUrl: './person.html',
  styleUrl: './person.scss',
})
export class PersonPage {
  private readonly api = inject(Api);
  private readonly title = inject(Title);

  /** TMDB person id (route parameter). */
  readonly id = input.required({ transform: numberAttribute });

  protected readonly person = signal<PersonDetails | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly department = signal('Acting');
  protected readonly media = signal<MediaFilter>('all');
  protected readonly order = signal<CreditOrder>(storedOrder());
  protected readonly bioExpanded = signal(false);
  protected readonly selected = signal<DiscoverItem | null>(null);

  protected readonly departmentList = computed(() => {
    const p = this.person();
    return p ? departments(p.credits, p.knownForDepartment) : [];
  });

  /** Movies, then series, each sorted; empty sections are left out. */
  protected readonly sections = computed(() => {
    const p = this.person();
    if (!p) return [];
    const department = this.department(), media = this.media();
    const inDepartment = sortCredits(p.credits.filter(c => c.department === department), this.order());
    return (['movie', 'series'] as const)
      .filter(kind => media === 'all' || media === kind)
      .map(kind => ({
        kind,
        label: kind === 'movie' ? 'Movies' : 'Series',
        credits: inDepartment.filter(c => c.kind === kind).map(credit => {
          const item = creditItem(credit);
          return { credit, item, key: discoverKey(item) };
        }),
      }))
      .filter(s => s.credits.length > 0);
  });

  /** Distinct library titles among the person's credits. */
  protected readonly libraryCount = computed(() => {
    const keys = new Set<string>();
    for (const c of this.person()?.credits ?? []) if (c.movieId || c.seriesId) keys.add(`${c.kind}-${c.tmdbId}`);
    return keys.size;
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => this.load(id));
    });
  }

  private async load(id: number) {
    this.person.set(null);
    this.error.set(null);
    this.bioExpanded.set(false);
    try {
      const p = await this.api.person(id);
      if (this.id() !== id) return;
      this.department.set(departments(p.credits, p.knownForDepartment)[0] ?? 'Acting');
      this.person.set(p);
      this.title.setTitle(`${p.name} · Krakarr`);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected ageOf(birthday: string, deathday?: string) { return age(birthday, deathday ? new Date(deathday + 'T00:00:00') : undefined); }

  /** Links every credit of the added title to its new library id. */
  protected markAdded(d: DiscoverItem) {
    this.selected.set(null);
    const libraryId = d.kind === 'movie' ? d.item.movieId : d.item.seriesId;
    this.person.update(p => p && {
      ...p,
      credits: p.credits.map(c => c.kind !== d.kind || c.tmdbId !== d.item.tmdbId ? c
        : d.kind === 'movie' ? { ...c, movieId: libraryId } : { ...c, seriesId: libraryId }),
    });
  }

  protected setOrder(order: CreditOrder) {
    this.order.set(order);
    try { localStorage.setItem(ORDER_KEY, order); } catch { /* storage unavailable */ }
  }

  protected back() { history.back(); }
}
