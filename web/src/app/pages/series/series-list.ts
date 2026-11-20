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
      <h1 class="page-title mr-auto">Series <span class="ml-1 text-base font-normal text-slate-500">{{ series().length }}</span></h1>
      <input class="input w-56" placeholder="Filter…" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing episodes</option>
        <option value="continuing">Continuing</option>
        <option value="ended">Ended</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      <a routerLink="/series/import" class="btn-secondary">Import library</a>
      <a routerLink="/series/add" class="btn-primary">+ Add series</a>
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
      <div class="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8">
        @for (s of filtered(); track s.id) {
          <a [routerLink]="['/series', s.id]" class="group">
            <div class="relative overflow-hidden rounded-lg ring-1 ring-surface-700 transition group-hover:ring-accent-500">
              <kr-poster [url]="s.posterUrl" [title]="s.title" />
              @if (s.downloadingCount > 0) {
                <span class="badge absolute top-2 right-2 bg-accent-500 text-slate-950">↓ {{ s.downloadingCount }}</span>
              }
            </div>
            <kr-progress class="mt-2" [value]="percent(s)" [color]="!s.monitored ? 'bg-slate-500' : s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500' : 'bg-rose-500'" />
            <div class="mt-1.5 truncate text-sm font-medium text-slate-100 group-hover:text-white">{{ s.title }}</div>
            <div class="flex justify-between text-xs text-slate-500">
              <span>{{ s.episodeFileCount }}/{{ s.episodeCount }} episodes</span>
              @if (s.nextAiring) { <span [title]="'Next airing'">{{ s.nextAiring | date: 'MMM d' }}</span> }
              @else { <span>{{ s.status }}</span> }
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
