import { Component, DestroyRef, OnInit, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { Movie, MovieCollection } from '../../core/models';
import { groupByCollection } from '../../core/movie-groups';
import { Toasts } from '../../core/toast';
import { Poster } from '../../shared/poster';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'downloaded' | 'unmonitored';
type Grouping = 'none' | 'collection';

const GROUPING_KEY = 'krakarr.movies.groupBy';

function storedGrouping(): Grouping {
  try { return localStorage.getItem(GROUPING_KEY) === 'collection' ? 'collection' : 'none'; } catch { return 'none'; }
}

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, NgTemplateOutlet, Poster, Progress],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Movies <span class="ml-1 text-base font-normal text-slate-500">{{ movies().length }}</span></h1>
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
      <div class="flex gap-2 max-sm:basis-full">
        <a routerLink="/movies/import" class="btn-secondary max-sm:flex-1">Import library</a>
        <a routerLink="/movies/add" class="btn-primary max-sm:flex-1">+ Add movie</a>
      </div>
    </div>

    @if (loading()) {
      <p class="text-slate-500">Loading…</p>
    } @else if (movies().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">Your movie library is empty</p>
        <p class="max-w-md text-sm text-slate-400">Add a root folder and a TMDB API key in Settings, connect an indexer, then add your first movie.</p>
        <div class="mt-2 flex gap-2">
          <a routerLink="/settings" class="btn-secondary">Settings</a>
          <a routerLink="/movies/add" class="btn-primary">Add movie</a>
        </div>
      </div>
    } @else if (groups(); as groups) {
      @for (g of groups; track g.key) {
        <section class="mb-8">
          <button type="button" class="mb-3 flex w-full items-center gap-2 border-b border-surface-700 pb-2 text-left"
                  [attr.aria-expanded]="!collapsed().has(g.key)" (click)="toggle(g.key)">
            <svg class="size-4 shrink-0 text-slate-500 transition-transform" [class.-rotate-90]="collapsed().has(g.key)" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
              <path fill-rule="evenodd" d="M5.23 7.21a.75.75 0 0 1 1.06.02L10 11.17l3.71-3.94a.75.75 0 1 1 1.08 1.04l-4.25 4.5a.75.75 0 0 1-1.08 0l-4.25-4.5a.75.75 0 0 1 .02-1.06Z" clip-rule="evenodd" />
            </svg>
            <h2 class="min-w-0 truncate text-base font-semibold text-white">{{ g.title }}</h2>
            <span class="shrink-0 text-xs text-slate-500">{{ g.downloaded }} / {{ g.movies.length }} downloaded</span>
          </button>
          @if (!collapsed().has(g.key)) {
            <div [class]="gridClass">
              @for (m of g.movies; track m.id) {
                <ng-container *ngTemplateOutlet="card; context: { $implicit: m }" />
              }
            </div>
          }
        </section>
      } @empty {
        <p class="text-slate-500">No movies match the filter.</p>
      }
    } @else {
      <div [class]="gridClass">
        @for (m of filtered(); track m.id) {
          <ng-container *ngTemplateOutlet="card; context: { $implicit: m }" />
        }
      </div>
    }

    <ng-template #card let-m>
          <a [routerLink]="['/movies', m.id]" class="group">
            <div class="relative overflow-hidden rounded-lg ring-1 ring-surface-700 transition group-hover:ring-accent-500">
              <kr-poster [url]="m.posterUrl" [title]="m.title" />
              <div class="absolute inset-x-0 bottom-0 h-1"
                   [class]="m.hasFile ? 'bg-emerald-500' : !m.monitored ? 'bg-slate-500' : m.downloadProgress != null ? 'bg-accent-500' : 'bg-rose-500'"></div>
              @if (m.downloadProgress != null) {
                <div class="absolute inset-x-2 bottom-3"><kr-progress [value]="m.downloadProgress" /></div>
              }
            </div>
            <div class="mt-1.5 truncate text-xs font-medium sm:mt-2 sm:text-sm text-slate-100 group-hover:text-white">{{ m.title }}</div>
            <div class="truncate text-[11px] text-slate-500 sm:text-xs">{{ m.year || '—' }}{{ m.movieFile ? ' · ' + m.movieFile.quality : '' }}</div>
          </a>
    </ng-template>
  `,
})
export class MovieListPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly movies = signal<Movie[]>([]);
  protected readonly loading = signal(true);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly grouping = signal<Grouping>(storedGrouping());
  protected readonly collections = signal<MovieCollection[]>([]);
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());
  protected readonly gridClass = 'grid grid-cols-3 gap-x-3 gap-y-4 sm:gap-4 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8';

  private readonly collectionNames = computed(() => new Map(this.collections().map(c => [c.id, c.name.toLowerCase()])));

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const names = this.collectionNames();
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q) && !(m.collectionId != null && names.get(m.collectionId)?.includes(q))) return false;
      switch (this.filter()) {
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

  /** Sections when grouping by collection; null for the flat grid. */
  protected readonly groups = computed(() => this.grouping() === 'collection' ? groupByCollection(this.filtered(), this.collections()) : null);

  constructor() {
    // Keep download progress live without refetching the whole library.
    effect(() => {
      const downloads = this.events.downloads();
      const byMovie = new Map<number, number>();
      for (const d of downloads) if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
      this.movies.update(list => list.map(m => {
        const p = byMovie.get(m.id);
        return p === m.downloadProgress || (p === undefined && m.downloadProgress == null) ? m : { ...m, downloadProgress: p };
      }));
    });
  }

  ngOnInit() {
    this.load();
    this.events.movieUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

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

  private async load() {
    try {
      const [movies, collections] = await Promise.all([this.api.movies(), this.api.movieCollections()]);
      this.movies.set(movies);
      this.collections.set(collections);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
