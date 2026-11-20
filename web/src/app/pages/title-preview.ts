import { Component, computed, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Title } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { Auth } from '../core/auth';
import { CastMember, CrewMember, DiscoverItem, MovieDetails, SeasonSummary, SeriesDetails } from '../core/models';
import { CreditsView } from '../shared/credits';
import { DiscoverAdd } from '../shared/discover-add';
import { Poster } from '../shared/poster';
import { RelatedTitles } from '../shared/related-titles';

type Kind = 'movie' | 'series';

/** What the page shows, common to movies and shows. */
interface Preview {
  item: DiscoverItem;
  title: string;
  year: number;
  overview?: string;
  tagline?: string;
  posterUrl?: string;
  fanartUrl?: string;
  rating?: number;
  facts: string[];
  genres: string[];
  imdbId?: string;
  tvdbId?: number;
  cast: CastMember[];
  crew: CrewMember[];
  seasons: SeasonSummary[];
}

/**
 * Detail page for any TMDB movie or show that is not in the library (discovery, recommendations, a person's credits).
 * Titles that are in the library redirect to their library page.
 */
@Component({
  selector: 'kr-title-preview',
  imports: [DatePipe, Poster, CreditsView, RelatedTitles, DiscoverAdd],
  template: `
    @if (error()) {
      <div class="card p-6 text-center">
        <p class="text-rose-300">{{ error() }}</p>
        <button class="btn-secondary mt-4" (click)="back()">Go back</button>
      </div>
    } @else if (preview(); as p) {
      <!-- Hero -->
      <div class="relative -mx-4 -mt-6 mb-6 overflow-hidden lg:-mx-8">
        @if (p.fanartUrl) { <img [src]="p.fanartUrl" alt="" class="absolute inset-0 h-full w-full object-cover opacity-20" /> }
        <div class="absolute inset-0 bg-gradient-to-t from-surface-950 via-surface-950/80 to-surface-950/40"></div>
        <div class="relative flex flex-col gap-5 px-4 py-6 sm:flex-row sm:gap-6 sm:py-8 lg:px-8">
          <kr-poster class="w-32 shrink-0 shadow-2xl sm:w-48" [url]="p.posterUrl" [title]="p.title" />
          <div class="min-w-0 flex-1">
            <h1 class="text-2xl font-bold tracking-tight text-white sm:text-3xl">{{ p.title }} @if (p.year) { <span class="font-normal text-slate-400">({{ p.year }})</span> }</h1>
            @if (p.tagline) { <p class="mt-1 text-sm italic text-slate-400">{{ p.tagline }}</p> }
            <div class="mt-2 flex flex-wrap items-center gap-2 text-sm text-slate-400">
              @for (f of p.facts; track $index) { @if ($index > 0) { <span>·</span> } <span>{{ f }}</span> }
              @if (p.rating) { @if (p.facts.length) { <span>·</span> } <span class="text-amber-300">★ {{ p.rating }}</span> }
              <span>·</span><a class="hover:text-white" [href]="tmdbUrl()" target="_blank" rel="noopener">TMDB</a>
              @if (p.tvdbId) { <span>·</span><a class="hover:text-white" [href]="'https://thetvdb.com/dereferrer/series/' + p.tvdbId" target="_blank" rel="noopener">TVDB</a> }
              @if (p.imdbId) { <span>·</span><a class="hover:text-white" [href]="'https://www.imdb.com/title/' + p.imdbId" target="_blank" rel="noopener">IMDb</a> }
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              <span class="badge" [class]="kind() === 'movie' ? 'bg-sky-500/15 text-sky-300' : 'bg-violet-500/15 text-violet-300'">{{ kind() === 'movie' ? 'Movie' : 'Series' }}</span>
              <span class="badge bg-slate-500/15 text-slate-400">Not in library</span>
              @for (g of p.genres; track g) { <span class="badge bg-surface-700 text-slate-300">{{ g }}</span> }
            </div>
            @if (p.overview) { <p class="mt-4 max-w-3xl text-sm leading-relaxed text-slate-300">{{ p.overview }}</p> }
            <div class="mt-5 flex flex-wrap gap-2">
              @if (canAdd()) { <button class="btn-primary" (click)="adding.set(true)">+ Add to library</button> }
              <button class="btn-ghost" (click)="back()">Back</button>
            </div>
          </div>
        </div>
      </div>

      <!-- Seasons (series) -->
      @if (p.seasons.length > 0) {
        <section class="mb-8">
          <h2 class="mb-3 text-lg font-semibold text-white">Seasons</h2>
          <div class="grid grid-cols-3 gap-3 sm:grid-cols-4 md:grid-cols-6 xl:grid-cols-8">
            @for (s of p.seasons; track s.seasonNumber) {
              <div class="min-w-0">
                <kr-poster [url]="s.posterUrl ?? p.posterUrl" [title]="s.name ?? 'Season ' + s.seasonNumber" />
                <div class="mt-1.5 truncate text-sm font-medium text-slate-100">{{ s.name ?? (s.seasonNumber === 0 ? 'Specials' : 'Season ' + s.seasonNumber) }}</div>
                <div class="text-xs text-slate-500">
                  {{ s.episodeCount }} {{ s.episodeCount === 1 ? 'episode' : 'episodes' }}
                  @if (s.airDate) { · {{ s.airDate | date: 'y' }} }
                </div>
              </div>
            }
          </div>
        </section>
      }

      <kr-credits [cast]="p.cast" [crew]="p.crew" />

      <kr-related-titles [kind]="kind()" [id]="tmdbId()" source="tmdb" />

      @if (adding()) {
        <kr-discover-add [item]="p.item" (closed)="adding.set(false)" (added)="openLibrary($event)" />
      }
    } @else {
      <p class="text-slate-500">Loading…</p>
    }
  `,
})
export class TitlePreviewPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly title = inject(Title);

  /** Route data. */
  readonly kind = input<Kind>('movie');
  /** Route parameter. */
  readonly tmdbId = input.required({ transform: numberAttribute });

  /** Adding to the library is for admins and managers. */
  protected readonly canAdd = inject(Auth).isStaff;
  protected readonly preview = signal<Preview | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly adding = signal(false);
  protected readonly tmdbUrl = computed(() => `https://www.themoviedb.org/${this.kind() === 'movie' ? 'movie' : 'tv'}/${this.tmdbId()}`);

  constructor() {
    // The component is reused when a related title links to another preview, so reload on every id change.
    effect(() => {
      const kind = this.kind(), id = this.tmdbId();
      untracked(() => this.load(kind, id));
    });
  }

  private async load(kind: Kind, tmdbId: number) {
    this.preview.set(null);
    this.error.set(null);
    this.adding.set(false);
    try {
      const preview = kind === 'movie' ? fromMovie(await this.api.tmdbMovie(tmdbId)) : fromSeries(await this.api.tmdbSeries(tmdbId));
      if (this.kind() !== kind || this.tmdbId() !== tmdbId) return;
      const libraryId = preview.item.kind === 'movie' ? preview.item.item.movieId : preview.item.item.seriesId;
      if (libraryId) {
        this.router.navigate([kind === 'movie' ? '/movies' : '/series', libraryId], { replaceUrl: true });
        return;
      }
      this.preview.set(preview);
      this.title.setTitle(`${preview.title} · Krakarr`);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected openLibrary(d: DiscoverItem) {
    this.adding.set(false);
    const id = d.kind === 'movie' ? d.item.movieId : d.item.seriesId;
    if (id) this.router.navigate([d.kind === 'movie' ? '/movies' : '/series', id], { replaceUrl: true });
  }

  protected back() { history.back(); }
}

function fromMovie(d: MovieDetails): Preview {
  const m = d.movie;
  const facts: string[] = [];
  if (d.releaseDate) facts.push(new Date(d.releaseDate + 'T00:00:00').toLocaleDateString(undefined, { dateStyle: 'medium' }));
  if (m.runtime) facts.push(`${m.runtime} min`);
  if (m.status && m.status !== 'Released') facts.push(m.status);
  return {
    item: { kind: 'movie', item: m }, title: m.title, year: m.year, overview: m.overview, tagline: d.tagline, posterUrl: m.posterUrl,
    fanartUrl: m.fanartUrl, rating: m.rating, facts, genres: d.genres, imdbId: m.imdbId, cast: d.cast, crew: d.crew, seasons: [],
  };
}

function fromSeries(d: SeriesDetails): Preview {
  const s = d.show;
  const facts: string[] = [];
  if (s.network) facts.push(s.network);
  if (d.runtime) facts.push(`${d.runtime} min`);
  if (s.status) facts.push(s.status);
  if (d.episodeCount) facts.push(`${d.episodeCount} episodes`);
  return {
    item: { kind: 'series', item: s }, title: s.title, year: s.year, overview: s.overview, tagline: d.tagline, posterUrl: s.posterUrl,
    fanartUrl: d.fanartUrl, rating: s.rating, facts, genres: d.genres, imdbId: d.imdbId, tvdbId: s.tvdbId, cast: d.cast, crew: d.crew,
    seasons: d.seasons,
  };
}
