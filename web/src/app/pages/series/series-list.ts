import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';
import { Library } from '../../core/library';
import { SeriesListItem } from '../../core/models';
import { NearViewport } from '../../shared/near-viewport';
import { Poster } from '../../shared/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../shared/poster-grid';
import { MoreMenu } from '../../shared/more-menu';
import { Progress } from '../../shared/progress';

type Filter = 'all' | 'missing' | 'continuing' | 'ended' | 'unmonitored';

@Component({
  selector: 'kr-series-list',
  imports: [RouterLink, FormsModule, DatePipe, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto max-sm:basis-full">Series <span class="ml-1 text-base font-normal text-slate-500">@if (!loading()) { {{ series().length }} }</span></h1>
      <input class="input min-w-0 flex-1 sm:w-56 sm:flex-none" placeholder="Filter…" type="search" [ngModel]="query()" (ngModelChange)="query.set($event)" />
      <select class="input w-36 sm:w-40" [ngModel]="filter()" (ngModelChange)="filter.set($event)">
        <option value="all">All</option>
        <option value="missing">Missing episodes</option>
        <option value="continuing">Continuing</option>
        <option value="ended">Ended</option>
        <option value="unmonitored">Unmonitored</option>
      </select>
      @if (auth.isStaff()) {
        <div class="flex gap-2 max-sm:basis-full">
          <a routerLink="/series/add" class="btn-primary max-sm:flex-1">+ Add series</a>
          <kr-more-menu>
            <a routerLink="/series/import" class="menu-item" role="menuitem">Import existing library…</a>
          </kr-more-menu>
        </div>
      }
    </div>

    @if (loading()) {
      <kr-poster-grid-skeleton />
    } @else if (series().length === 0) {
      <div class="card flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-lg font-medium text-white">No series yet</p>
        @if (auth.isStaff()) {
          <p class="max-w-md text-sm text-slate-400">Add a TV root folder and a TMDB API key in Settings, then add your first show.</p>
          <div class="mt-2 flex gap-2">
            <a routerLink="/settings" class="btn-secondary">Settings</a>
            <a routerLink="/series/import" class="btn-secondary">Import existing</a>
            <a routerLink="/series/add" class="btn-primary">Add series</a>
          </div>
        } @else {
          <p class="max-w-md text-sm text-slate-400">Nothing here yet. Browse <a routerLink="/discover" class="underline">Discover</a> for ideas.</p>
        }
      </div>
    } @else if (filtered().length === 0) {
      <p class="py-12 text-center text-slate-500">No series match the filter.</p>
    } @else {
      <div [class]="grid">
        @for (s of visible(); track s.id) {
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
      @if (visible().length < filtered().length) {
        <div class="h-px" (krNearViewport)="showMore()"></div>
      }
    }
  `,
})
export class SeriesListPage {
  protected readonly auth = inject(Auth);
  private readonly cache = inject(Library).series;

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.cache.items() === null);
  protected readonly series = computed(() => this.cache.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    return this.series().filter(s => {
      if (q && !s.title.toLowerCase().includes(q)) return false;
      switch (filter) {
        case 'missing': return s.monitored && s.episodeFileCount < s.episodeCount;
        case 'continuing': return s.status !== 'Ended';
        case 'ended': return s.status === 'Ended';
        case 'unmonitored': return !s.monitored;
        default: return true;
      }
    });
  });

  /** Cards rendered so far: more are added as the end of the grid scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter()], computation: () => GRID_CHUNK });
  protected readonly visible = computed(() => this.filtered().slice(0, this.limit()));

  constructor() {
    inject(DestroyRef).onDestroy(this.cache.attach());
  }

  protected percent(s: SeriesListItem) { return s.episodeCount === 0 ? 0 : (s.episodeFileCount / s.episodeCount) * 100; }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }
}
