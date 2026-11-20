import { Component, DestroyRef, OnInit, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { Movie } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Poster } from '../../shared/poster';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'downloaded' | 'unmonitored';

@Component({
  selector: 'kr-movie-list',
  imports: [RouterLink, FormsModule, Poster, Progress],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Movies <span class="ml-1 text-base font-normal text-slate-500">{{ movies().length }}</span></h1>
      <input class="input min-w-0 flex-1 sm:w-56 sm:flex-none" placeholder="Filter…" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input w-36 sm:w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing</option>
        <option value="downloaded">Downloaded</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      @if (auth.isStaff()) {
        <div class="flex gap-2 max-sm:basis-full">
          <a routerLink="/movies/import" class="btn-secondary max-sm:flex-1">Import library</a>
          <a routerLink="/movies/add" class="btn-primary max-sm:flex-1">+ Add movie</a>
        </div>
      }
    </div>

    @if (loading()) {
      <p class="text-slate-500">Loading…</p>
    } @else if (movies().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">Your movie library is empty</p>
        @if (auth.isStaff()) {
          <p class="max-w-md text-sm text-slate-400">Add a root folder and a TMDB API key in Settings, connect an indexer, then add your first movie.</p>
          <div class="mt-2 flex gap-2">
            <a routerLink="/settings" class="btn-secondary">Settings</a>
            <a routerLink="/movies/add" class="btn-primary">Add movie</a>
          </div>
        } @else {
          <p class="max-w-md text-sm text-slate-400">Nothing here yet. Browse <a routerLink="/discover" class="underline">Discover</a> for ideas.</p>
        }
      </div>
    } @else {
      <div class="grid grid-cols-3 gap-x-3 gap-y-4 sm:gap-4 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8">
        @for (m of filtered(); track m.id) {
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
        }
      </div>
    }
  `,
})
export class MovieListPage implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly movies = signal<Movie[]>([]);
  protected readonly loading = signal(true);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    return this.movies().filter(m => {
      if (q && !m.title.toLowerCase().includes(q)) return false;
      switch (this.filter()) {
        case 'missing': return m.monitored && !m.hasFile;
        case 'downloaded': return m.hasFile;
        case 'unmonitored': return !m.monitored;
        default: return true;
      }
    });
  });

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

  private async load() {
    try {
      this.movies.set(await this.api.movies());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
