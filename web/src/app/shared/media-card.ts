import { Component, computed, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Auth } from '../core/auth';
import { DiscoverItem } from '../core/models';
import { Poster } from './poster';

export function discoverKey(d: DiscoverItem): string {
  return `${d.kind}-${d.item.tmdbId ?? 'tvdb' + (d.kind === 'series' ? d.item.tvdbId : '')}`;
}

/** Route of the library title a discovered item corresponds to, or null when it is not in the library. */
export function libraryRoute(d: DiscoverItem): (string | number)[] | null {
  if (d.kind === 'movie') return d.item.movieId ? ['/movies', d.item.movieId] : null;
  return d.item.seriesId ? ['/series', d.item.seriesId] : null;
}

/** Detail page of a discovered item: the library page when it is in the library, otherwise its TMDB page. */
export function detailRoute(d: DiscoverItem): (string | number)[] | null {
  const library = libraryRoute(d);
  if (library) return library;
  if (d.item.tmdbId == null) return null;
  return [d.kind === 'movie' ? '/movies' : '/series', 'tmdb', d.item.tmdbId];
}

/** Poster card for a discovered movie or show: links to its detail page and offers to add it when not in the library. */
@Component({
  selector: 'kr-media-card',
  imports: [RouterLink, Poster],
  template: `
    @let d = item();
    <div class="group relative overflow-hidden rounded-lg ring-1 ring-surface-700 transition hover:ring-accent-500">
      <kr-poster [url]="d.item.posterUrl" [title]="d.item.title" />
      @if (detail(); as r) {
        <a [routerLink]="r" class="absolute inset-0" [attr.aria-label]="d.item.title"></a>
      }
      <div class="pointer-events-none absolute inset-x-0 top-0 flex items-start justify-between p-1.5">
        <span class="badge text-[10px] uppercase tracking-wide backdrop-blur"
              [class]="d.kind === 'movie' ? 'bg-sky-500/80 text-white' : 'bg-violet-500/80 text-white'">{{ d.kind === 'movie' ? 'Movie' : 'Series' }}</span>
        @if (route()) {
          <span class="badge bg-emerald-500/90 text-white" title="In library">✓</span>
        }
      </div>
      <!-- Hover / focus details (touch screens get the button under the title instead) -->
      <div class="pointer-events-none absolute inset-0 flex flex-col justify-end bg-gradient-to-t from-surface-950 via-surface-950/85 to-surface-950/10 p-3 opacity-0 transition group-hover:opacity-100 group-focus-within:opacity-100 pointer-coarse:hidden">
        <div class="text-xs text-slate-400">
          {{ d.item.year || '' }}
          @if (d.item.rating) { <span class="ml-1 text-amber-300">★ {{ d.item.rating }}</span> }
        </div>
        <div class="line-clamp-2 text-sm font-semibold text-white">{{ d.item.title }}</div>
        <p class="mt-1 line-clamp-4 text-xs text-slate-300">{{ d.item.overview }}</p>
        @if (route(); as r) {
          <a [routerLink]="r" class="btn-secondary btn-sm pointer-events-auto mt-2 justify-center">Open</a>
        } @else if (canAdd()) {
          <button class="btn-primary btn-sm pointer-events-auto mt-2 justify-center" (click)="add.emit(d)">+ Add</button>
        }
      </div>
    </div>
    @if (detail(); as r) {
      <a [routerLink]="r" class="mt-2 block truncate text-sm font-medium text-slate-100 hover:text-accent-300" [title]="d.item.title">{{ d.item.title }}</a>
    } @else {
      <div class="mt-2 truncate text-sm font-medium text-slate-100" [title]="d.item.title">{{ d.item.title }}</div>
    }
    <div class="text-xs text-slate-500">
      {{ d.item.year || '—' }}
      @if (d.item.rating) { <span class="ml-1">★ {{ d.item.rating }}</span> }
    </div>
    @if (caption()) { <div class="truncate text-xs text-slate-400" [title]="caption()">{{ caption() }}</div> }
    <div class="mt-1.5 hidden pointer-coarse:block">
      @if (route(); as r) {
        <a [routerLink]="r" class="btn-secondary btn-sm w-full">Open</a>
      } @else if (canAdd()) {
        <button class="btn-primary btn-sm w-full" (click)="add.emit(d)">+ Add</button>
      }
    </div>
  `,
  host: { class: 'block' },
})
export class MediaCard {
  /** Adding to the library is for admins and managers; plain users will request titles instead. */
  protected readonly canAdd = inject(Auth).isStaff;
  readonly item = input.required<DiscoverItem>();
  /** Optional line under the year, e.g. the character a person plays. */
  readonly caption = input<string | null | undefined>(null);
  readonly add = output<DiscoverItem>();
  protected readonly route = computed(() => libraryRoute(this.item()));
  protected readonly detail = computed(() => detailRoute(this.item()));
}
