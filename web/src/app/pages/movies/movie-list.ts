import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Events } from '../../core/events';
import { Auth } from '../../core/auth';
import { Library } from '../../core/library';
import { MovieListItem } from '../../core/models';
import { MovieGroup, groupByCollection } from '../../core/movie-groups';
import { NearViewport } from '../../shared/near-viewport';
import { Poster } from '../../shared/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../shared/poster-grid';
import { MoreMenu } from '../../shared/more-menu';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'downloaded' | 'unmonitored';
type Grouping = 'none' | 'collection';

const GROUPING_KEY = 'krakarr.movies.groupBy';

function storedGrouping(): Grouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, NgTemplateOutlet, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Movies <span class="ml-1 text-base font-normal text-slate-500">@if (!loading()) { {{ movies().length }} }</span></h1>
      <input class="input min-w-0 flex-1 max-sm:basis-full sm:w-56 sm:flex-none" placeholder="Filter…" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input min-w-0 max-sm:flex-1 sm:w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing</option>
        <option value="downloaded">Downloaded</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      <select class="input min-w-0 max-sm:flex-1 sm:w-44" aria-label="Group by" [ngModel]="grouping()" (ngModelChange)="setGrouping($event)">
        <option value="none">Ungrouped</option>
        <option value="collection">By collection</option>
      </select>
      @if (auth.isStaff()) {
        <div class="flex gap-2 max-sm:basis-full">
          <a routerLink="/movies/add" class="btn-primary max-sm:flex-1">+ Add movie</a>
          <kr-more-menu>
            <a routerLink="/movies/import" class="menu-item" role="menuitem">Import existing library…</a>
          </kr-more-menu>
        </div>
      }
    </div>

    @if (loading()) {
      <kr-poster-grid-skeleton />
    } @else if (movies().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">Your movie library is empty</p>
        @if (auth.isStaff()) {
          <p class="max-w-md text-sm text-slate-400">Add a root folder and a TMDB API key in Settings, connect an indexer, then add your first movie.</p>
          <div class="mt-2 flex gap-2">
            <a routerLink="/settings" class="btn-secondary">Settings</a>
            <a routerLink="/movies/import" class="btn-secondary">Import existing</a>
            <a routerLink="/movies/add" class="btn-primary">Add movie</a>
          </div>
        } @else {
          <p class="max-w-md text-sm text-slate-400">Nothing here yet. Browse <a routerLink="/discover" class="underline">Discover</a> for ideas.</p>
        }
      </div>
    } @else if (filtered().length === 0) {
      <p class="py-12 text-center text-slate-500">No movies match the filter.</p>
    } @else if (visibleGroups(); as groups) {
      @for (g of groups; track g.group.key) {
        <section class="mb-8">
          <button type="button" class="mb-3 flex w-full items-center gap-2 border-b border-surface-700 pb-2 text-left"
                  [attr.aria-expanded]="!collapsed().has(g.group.key)" (click)="toggle(g.group.key)">
            <svg class="size-4 shrink-0 text-slate-500 transition-transform" [class.-rotate-90]="collapsed().has(g.group.key)" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fill-rule="evenodd" d="M5.23 7.21a.75.75 0 0 1 1.06.02L10 11.17l3.71-3.94a.75.75 0 1 1 1.08 1.04l-4.25 4.5a.75.75 0 0 1-1.08 0l-4.25-4.5a.75.75 0 0 1 .02-1.06Z" clip-rule="evenodd" />
            </svg>
            <h2 class="min-w-0 truncate text-base font-semibold text-white">{{ g.group.title }}</h2>
            <span class="shrink-0 text-xs text-slate-500">{{ g.group.downloaded }} / {{ g.group.movies.length }} downloaded</span>
          </button>
          @if (g.movies.length) {
            <div [class]="grid">
              @for (m of g.movies; track m.id) {
                <ng-container *ngTemplateOutlet="card; context: { $implicit: m }" />
              }
            </div>
          }
        </section>
      }
      @if (hasMore()) {
        <div class="h-px" (krNearViewport)="showMore()"></div>
      }
    } @else {
      <div [class]="grid">
        @for (m of visible(); track m.id) {
          <ng-container *ngTemplateOutlet="card; context: { $implicit: m }" />
        }
      </div>
      @if (hasMore()) {
        <div class="h-px" (krNearViewport)="showMore()"></div>
      }
    }

    <ng-template #card let-m>
      @let progress = progressOf(m);
      <a [routerLink]="['/movies', m.id]" class="group">
        <div class="relative overflow-hidden rounded-lg ring-1 ring-surface-700 transition group-hover:ring-accent-500">
          <kr-poster [url]="m.posterUrl" [title]="m.title" />
          <div class="absolute inset-x-0 bottom-0 h-1"
               [class]="m.hasFile ? 'bg-emerald-500' : !m.monitored ? 'bg-slate-500' : progress != null ? 'bg-accent-500' : 'bg-rose-500'"></div>
          @if (progress != null) {
            <div class="absolute inset-x-2 bottom-3"><kr-progress [value]="progress" /></div>
          }
        </div>
        <div class="mt-1.5 truncate text-xs font-medium sm:mt-2 sm:text-sm text-slate-100 group-hover:text-white">{{ m.title }}</div>
        <div class="truncate text-[11px] text-slate-500 sm:text-xs">{{ m.year || '—' }}{{ m.quality ? ' · ' + m.quality : '' }}</div>
      </a>
    </ng-template>
  `,
})
export class MovieListPage {
  protected readonly auth = inject(Auth);
  private readonly events = inject(Events);
  private readonly library = inject(Library);

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.library.movies.items() === null);
  protected readonly movies = computed(() => this.library.movies.items() ?? []);
  private readonly collections = computed(() => this.library.movieCollections.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly grouping = signal<Grouping>(storedGrouping());
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());

  private readonly collectionNames = computed(() => new Map(this.collections().map(c => [c.id, c.name.toLowerCase()])));

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    const names = this.collectionNames();
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q) && !(m.collectionId != null && names.get(m.collectionId)?.includes(q))) return false;
      switch (filter) {
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

  /** Cards rendered so far: more are added as the end of the list scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter(), this.grouping()], computation: () => GRID_CHUNK });

  /** Flat grid: the first `limit` movies. */
  protected readonly visible = computed(() => this.filtered().slice(0, this.limit()));

  /** Sections when grouping by collection; null for the flat grid. */
  private readonly groups = computed(() => this.grouping() === 'collection' ? groupByCollection(this.filtered(), this.collections()) : null);

  /** The sections with the first `limit` cards spread over the expanded ones; null for the flat grid. */
  protected readonly visibleGroups = computed(() => {
    const groups = this.groups();
    if (!groups) return null;
    const collapsed = this.collapsed();
    let left = this.limit();
    const result: { group: MovieGroup; movies: MovieListItem[] }[] = [];
    for (const group of groups) {
      if (left <= 0) break;
      const movies = collapsed.has(group.key) ? [] : group.movies.slice(0, left);
      left -= movies.length;
      result.push({ group, movies });
    }
    return result;
  });

  protected readonly hasMore = computed(() => {
    const visible = this.visibleGroups();
    if (!visible) return this.visible().length < this.filtered().length;
    const collapsed = this.collapsed();
    return visible.length < this.groups()!.length
      || visible.some(g => !collapsed.has(g.group.key) && g.movies.length < g.group.movies.length);
  });

  /** Live download progress per movie, while connected; otherwise the list's own snapshot is used. */
  private readonly liveProgress = computed(() => {
    if (!this.events.connected()) return null;
    const byMovie = new Map<number, number>();
    for (const d of this.events.downloads()) if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
    return byMovie;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(this.library.movies.attach());
    destroyRef.onDestroy(this.library.movieCollections.attach());
  }

  protected progressOf(m: MovieListItem): number | null {
    const live = this.liveProgress();
    return live ? (live.get(m.id) ?? null) : (m.downloadProgress ?? null);
  }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }

  protected setGrouping(value: Grouping) {
    this.grouping.set(value);
    try { localStorage.setItem(GROUPING_KEY, value); } catch { /* storage unavailable */ }
  }

  protected toggle(key: string) {
    this.collapsed.update(set => {
      const next = new Set(set);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }
}
