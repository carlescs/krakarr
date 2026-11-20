import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { Series } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Poster } from '../../shared/poster';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'continuing' | 'ended' | 'unmonitored';

@Component({
  selector: 'kr-series-list',
  imports: [RouterLink, FormsModule, DatePipe, Poster, Progress],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Series <span class="ml-1 text-base font-normal text-slate-500">{{ series().length }}</span></h1>
      <input class="input min-w-0 flex-1 sm:w-56 sm:flex-none" placeholder="Filter…" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input w-36 sm:w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing episodes</option>
        <option value="continuing">Continuing</option>
        <option value="ended">Ended</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      <div class="flex gap-2 max-sm:basis-full">
        <a routerLink="/series/import" class="btn-secondary max-sm:flex-1">Import library</a>
        <a routerLink="/series/add" class="btn-primary max-sm:flex-1">+ Add series</a>
      </div>
    </div>

    @if (loading()) {
      <p class="text-slate-500">Loading…</p>
    } @else if (series().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">No series yet</p>
        <p class="max-w-md text-sm text-slate-400">Add a TV root folder and a TMDB API key in Settings, then add your first show.</p>
        <div class="mt-2 flex gap-2">
          <a routerLink="/settings" class="btn-secondary">Settings</a>
          <a routerLink="/series/add" class="btn-primary">Add series</a>
        </div>
      </div>
    } @else {
      <div class="grid grid-cols-3 gap-x-3 gap-y-4 sm:gap-4 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8">
        @for (s of filtered(); track s.id) {
          <a [routerLink]="['/series', s.id]" class="group">
            <div class="relative overflow-hidden rounded-lg ring-1 ring-surface-700 transition group-hover:ring-accent-500">
              <kr-poster [url]="s.posterUrl" [title]="s.title" />
              @if (s.downloadingCount > 0) {
                <span class="badge absolute top-1.5 right-1.5 bg-accent-500 sm:top-2 sm:right-2 text-slate-950">↓ {{ s.downloadingCount }}</span>
              }
            </div>
            <kr-progress class="mt-2" [value]="percent(s)" [color]="!s.monitored ? 'bg-slate-500' : s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500' : 'bg-rose-500'" />
            <div class="mt-1.5 truncate text-xs font-medium text-slate-100 group-hover:text-white sm:text-sm">{{ s.title }}</div>
            <div class="flex justify-between gap-1 text-[11px] text-slate-500 sm:text-xs">
              <span class="truncate">{{ s.episodeFileCount }}/{{ s.episodeCount }}<span class="max-sm:hidden"> episodes</span></span>
              @if (s.nextAiring) { <span class="whitespace-nowrap" [title]="'Next airing'">{{ s.nextAiring | date: 'MMM d' }}</span> }
              @else { <span class="truncate">{{ s.status }}</span> }
            </div>
          </a>
        }
      </div>
    }
  `,
})
export class SeriesListPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly series = signal<Series[]>([]);
  protected readonly loading = signal(true);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    return this.series().filter(s => {
      if (q && !s.title.toLowerCase().includes(q)) return false;
      switch (this.filter()) {
        case 'missing': return s.monitored && s.episodeFileCount < s.episodeCount;
        case 'continuing': return s.status !== 'Ended';
        case 'ended': return s.status === 'Ended';
        case 'unmonitored': return !s.monitored;
        default: return true;
      }
    });
  });

  protected percent(s: Series) { return s.episodeCount === 0 ? 0 : (s.episodeFileCount / s.episodeCount) * 100; }

  ngOnInit() {
    this.load();
    this.events.seriesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  private async load() {
    try { this.series.set(await this.api.seriesList()); }
    catch (e) { this.toasts.error(errorMessage(e)); }
    finally { this.loading.set(false); }
  }
}
