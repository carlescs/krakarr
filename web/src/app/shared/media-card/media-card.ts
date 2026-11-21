import { Component, computed, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';
import { DiscoverItem } from '../../core/models';
import { Poster } from '../poster/poster';

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

/** Poster card for a discovered movie or show: links to its detail page and offers to add (or request) it when not in the library. */
@Component({
  selector: 'kr-media-card',
  imports: [RouterLink, Poster],
  templateUrl: './media-card.html',
  styleUrl: './media-card.scss',
  host: { class: 'block' },
})
export class MediaCard {
  /** Admins and managers add titles to the library; plain users request them (the add output opens the right dialog). */
  protected readonly staff = inject(Auth).isStaff;
  readonly item = input.required<DiscoverItem>();
  /** Optional line under the year, e.g. the character a person plays. */
  readonly caption = input<string | null | undefined>(null);
  readonly add = output<DiscoverItem>();
  protected readonly route = computed(() => libraryRoute(this.item()));
  protected readonly detail = computed(() => detailRoute(this.item()));
}
