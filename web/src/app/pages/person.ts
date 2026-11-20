import { Component, computed, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Title } from '@angular/platform-browser';
import { Api, errorMessage } from '../core/api';
import { DiscoverItem, PersonCredit, PersonDetails } from '../core/models';
import { DiscoverAdd } from '../shared/discover-add';
import { MediaCard, discoverKey } from '../shared/media-card';

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
  template: `
    @if (error()) {
      <div class="card p-6 text-center">
        <p class="text-rose-300">{{ error() }}</p>
        <button class="btn-secondary mt-4" (click)="back()">Go back</button>
      </div>
    } @else if (person(); as p) {
      <div class="mb-8 flex flex-col gap-5 sm:flex-row sm:gap-6">
        <div class="aspect-[2/3] w-32 shrink-0 overflow-hidden rounded-lg bg-surface-800 shadow-2xl sm:w-48">
          @if (p.profileUrl) {
            <img [src]="p.profileUrl" [alt]="p.name" class="h-full w-full object-cover" />
          } @else {
            <div class="flex h-full w-full items-center justify-center bg-gradient-to-br from-surface-700 to-surface-850 text-4xl font-semibold text-slate-500">{{ p.name[0] }}</div>
          }
        </div>
        <div class="min-w-0 flex-1">
          <h1 class="text-2xl font-bold tracking-tight text-white sm:text-3xl">{{ p.name }}</h1>
          <div class="mt-2 flex flex-wrap items-center gap-2 text-sm text-slate-400">
            @if (p.knownForDepartment) { <span>{{ p.knownForDepartment }}</span><span>·</span> }
            @if (p.birthday) {
              <span>Born {{ p.birthday | date: 'mediumDate' }}@if (!p.deathday) { (age {{ ageOf(p.birthday) }}) }</span><span>·</span>
            }
            @if (p.deathday) {
              <span>Died {{ p.deathday | date: 'mediumDate' }}@if (p.birthday) { (aged {{ ageOf(p.birthday, p.deathday) }}) }</span><span>·</span>
            }
            @if (p.placeOfBirth) { <span>{{ p.placeOfBirth }}</span><span>·</span> }
            <a class="hover:text-white" [href]="'https://www.themoviedb.org/person/' + p.tmdbId" target="_blank" rel="noopener">TMDB</a>
            @if (p.imdbId) { <span>·</span><a class="hover:text-white" [href]="'https://www.imdb.com/name/' + p.imdbId" target="_blank" rel="noopener">IMDb</a> }
          </div>
          @if (libraryCount()) {
            <div class="mt-3"><span class="badge bg-emerald-500/15 text-emerald-300">{{ libraryCount() }} in your library</span></div>
          }
          @if (p.biography) {
            <p class="mt-4 max-w-3xl whitespace-pre-line text-sm leading-relaxed text-slate-300" [class.line-clamp-6]="!bioExpanded()">{{ p.biography }}</p>
            @if (p.biography.length > 500) {
              <button class="mt-1 text-sm text-accent-300 hover:text-accent-200" (click)="bioExpanded.set(!bioExpanded())">{{ bioExpanded() ? 'Show less' : 'Read more' }}</button>
            }
          }
        </div>
      </div>

      <div class="mb-4 flex flex-wrap items-center gap-2">
        @for (d of departmentList(); track d) {
          <button class="btn-sm" [class]="department() === d ? 'btn-primary' : 'btn-secondary'" (click)="department.set(d)">{{ d }}</button>
        }
        <div class="flex-1"></div>
        <select class="input w-auto" [value]="media()" (change)="media.set($any($event.target).value)" aria-label="Media type">
          <option value="all">Movies & series</option>
          <option value="movie">Movies</option>
          <option value="series">Series</option>
        </select>
        <select class="input w-auto" [value]="order()" (change)="setOrder($any($event.target).value)" aria-label="Sort by">
          <option value="newest">Newest first</option>
          <option value="oldest">Oldest first</option>
          <option value="title">Title</option>
          <option value="rating">Rating</option>
        </select>
      </div>

      @if (sections().length === 0) {
        <p class="text-sm text-slate-500">No credits to show.</p>
      }
      @for (s of sections(); track s.kind) {
        <section class="mb-8">
          <h2 class="mb-3 flex items-baseline gap-2 text-lg font-semibold text-white">
            {{ s.label }} <span class="text-sm font-normal text-slate-500">{{ s.credits.length }}</span>
          </h2>
          <div class="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-6 2xl:grid-cols-8">
            @for (c of s.credits; track c.key) {
              <kr-media-card [item]="c.item" [caption]="c.credit.role" (add)="selected.set($event)" />
            }
          </div>
        </section>
      }

      @if (selected(); as d) {
        <kr-discover-add [item]="d" (closed)="selected.set(null)" (added)="markAdded($event)" />
      }
    } @else {
      <p class="text-slate-500">Loading…</p>
    }
  `,
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
