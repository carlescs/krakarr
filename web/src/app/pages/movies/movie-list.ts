import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Events } from '../../core/events';
import { Library } from '../../core/library';
import { MovieListItem } from '../../core/models';
import { NearViewport } from '../../shared/near-viewport';
import { Poster } from '../../shared/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../shared/poster-grid';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'downloaded' | 'unmonitored';

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, Poster, Progress, NearViewport, PosterGridSkeleton],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Movies <span class="ml-1 text-base font-normal text-slate-500">@if (!loading()) { {{ movies().length }} }</span></h1>
      <input class="input min-w-0 flex-1 sm:w-56 sm:flex-none" placeholder="Filter…" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input w-36 sm:w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing</option>
        <option value="downloaded">Downloaded</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      <div class="flex gap-2 max-sm:basis-full">
        <a routerLink="/movies/import" class="btn-secondary max-sm:flex-1">Import library</a>
        <a routerLink="/movies/add" class="btn-primary max-sm:flex-1">+ Add movie</a>
      </div>
    </div>

    @if (loading()) {
      <kr-poster-grid-skeleton />
    } @else if (movies().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">Your movie library is empty</p>
        <p class="max-w-md text-sm text-slate-400">Add a root folder and a TMDB API key in Settings, connect an indexer, then add your first movie.</p>
        <div class="mt-2 flex gap-2">
          <a routerLink="/settings" class="btn-secondary">Settings</a>
          <a routerLink="/movies/add" class="btn-primary">Add movie</a>
        </div>
      </div>
    } @else if (filtered().length === 0) {
      <p class="py-12 text-center text-slate-500">No movies match the filter.</p>
    } @else {
      <div [class]="grid">
        @for (m of visible(); track m.id) {
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
        }
      </div>
      @if (visible().length < filtered().length) {
        <div class="h-px" (krNearViewport)="showMore()"></div>
      }
    }
  `,
})
export class MovieListPage {
  private readonly events = inject(Events);
  private readonly cache = inject(Library).movies;

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.cache.items() === null);
  protected readonly movies = computed(() => this.cache.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q)) return false;
      switch (filter) {
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

  /** Cards rendered so far: more are added as the end of the grid scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter()], computation: () => GRID_CHUNK });
  protected readonly visible = computed(() => this.filtered().slice(0, this.limit()));

  /** Live download progress per movie, while connected; otherwise the list's own snapshot is used. */
  private readonly liveProgress = computed(() => {
    if (!this.events.connected()) return null;
    const byMovie = new Map<number, number>();
    for (const d of this.events.downloads()) if (d.movieId && !d.imported) byMovie.set(d.movieId, Math.max(byMovie.get(d.movieId) ?? 0, d.progress));
    return byMovie;
  });

  constructor() {
    inject(DestroyRef).onDestroy(this.cache.attach());
  }

  protected progressOf(m: MovieListItem): number | null {
    const live = this.liveProgress();
    return live ? (live.get(m.id) ?? null) : (m.downloadProgress ?? null);
  }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }
}
