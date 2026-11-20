import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { SeriesLookup } from '../../core/models';
import { AddSeriesModal } from '../../shared/add-series-modal';
import { Poster } from '../../shared/poster';

@Component({
  selector: 'kr-add-series',
  imports: [FormsModule, RouterLink, Poster, AddSeriesModal],
  template: `
    <h1 class="page-title mb-6">Add series</h1>
    <form class="mb-8 flex gap-3" (ngSubmit)="search()">
      <input class="input flex-1 !py-3 !text-base" name="term" [(ngModel)]="term" placeholder="Search for a show, e.g. “Breaking Bad”" autofocus />
      <button class="btn-primary px-6" [disabled]="searching() || !term.trim()">{{ searching() ? 'Searching…' : 'Search' }}</button>
    </form>

    @if (error()) {
      <div class="card mb-6 border-rose-500/30 px-5 py-4 text-sm text-rose-300">
        {{ error() }} @if (error()!.includes('TMDB') || error()!.includes('TheTVDB')) { — <a routerLink="/settings" class="underline">open settings</a> }
      </div>
    }

    <div class="space-y-3">
      @for (r of results(); track (r.tmdbId ?? 0) + '-' + (r.tvdbId ?? 0)) {
        <div class="card flex gap-4 p-4">
          <kr-poster class="w-20 shrink-0 sm:w-24" [url]="r.posterUrl" [title]="r.title" />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <h2 class="text-lg font-semibold text-white">{{ r.title }}</h2>
              <span class="text-slate-400">{{ r.year || '' }}</span>
            </div>
            <div class="text-xs text-slate-500">{{ r.network }}{{ r.network && r.status ? ' · ' : '' }}{{ r.status }}</div>
            <p class="mt-2 line-clamp-3 text-sm text-slate-400">{{ r.overview }}</p>
          </div>
          <div class="shrink-0 self-center">
            @if (r.seriesId) {
              <a [routerLink]="['/series', r.seriesId]" class="btn-secondary">In library</a>
            } @else {
              <button class="btn-primary" (click)="selected.set(r)">Add</button>
            }
          </div>
        </div>
      }
    </div>

    @if (selected(); as show) {
      <kr-add-series-modal [show]="show" (closed)="selected.set(null)" (added)="router.navigate(['/series', $event.id])" />
    }
  `,
})
export class AddSeriesPage {
  private readonly api = inject(Api);
  protected readonly router = inject(Router);

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<SeriesLookup[]>([]);
  protected readonly selected = signal<SeriesLookup | null>(null);

  async search() {
    if (!this.term.trim()) return;
    this.searching.set(true);
    this.error.set(null);
    try {
      this.results.set(await this.api.lookupSeries(this.term.trim()));
      if (this.results().length === 0) this.error.set('No series found.');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }
}
