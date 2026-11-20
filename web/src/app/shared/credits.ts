import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { CastMember, CrewMember } from '../core/models';

/** Key crew and a scrolling row of the cast, each linking to the person's page. */
@Component({
  selector: 'kr-credits',
  imports: [RouterLink],
  template: `
    @if (crew().length > 0) {
      <section class="mb-6">
        <h2 class="mb-3 text-lg font-semibold text-white">Crew</h2>
        <div class="grid grid-cols-2 gap-x-4 gap-y-3 sm:grid-cols-3 lg:grid-cols-5">
          @for (c of crew(); track c.tmdbId) {
            <a [routerLink]="['/people', c.tmdbId]" class="group min-w-0">
              <div class="truncate text-sm font-medium text-slate-100 group-hover:text-accent-300">{{ c.name }}</div>
              <div class="truncate text-xs text-slate-500" [title]="c.jobs">{{ c.jobs }}</div>
            </a>
          }
        </div>
      </section>
    }
    @if (loading()) {
      <section class="mb-8">
        <h2 class="mb-3 text-lg font-semibold text-white">Cast</h2>
        <div class="flex gap-3 overflow-hidden">
          @for (i of placeholders; track i) { <div class="aspect-[2/3] w-24 shrink-0 animate-pulse rounded-lg bg-surface-800 sm:w-28"></div> }
        </div>
      </section>
    } @else if (cast().length > 0) {
      <section class="mb-8">
        <h2 class="mb-3 text-lg font-semibold text-white">Cast</h2>
        <div class="-mx-1 flex snap-x gap-3 overflow-x-auto px-1 pt-1 pb-2 [scrollbar-width:thin]">
          @for (c of cast(); track c.tmdbId) {
            <a [routerLink]="['/people', c.tmdbId]" class="group w-24 shrink-0 snap-start sm:w-28">
              <div class="aspect-[2/3] overflow-hidden rounded-lg bg-surface-800 ring-1 ring-surface-700 transition group-hover:ring-accent-500">
                @if (c.profileUrl) {
                  <img [src]="c.profileUrl" [alt]="c.name" loading="lazy" class="h-full w-full object-cover" />
                } @else {
                  <div class="flex h-full w-full items-center justify-center bg-gradient-to-br from-surface-700 to-surface-850 text-2xl font-semibold text-slate-500">{{ initials(c.name) }}</div>
                }
              </div>
              <div class="mt-1.5 truncate text-sm font-medium text-slate-100 group-hover:text-accent-300" [title]="c.name">{{ c.name }}</div>
              @if (c.character) { <div class="line-clamp-2 text-xs text-slate-400" [title]="c.character">{{ c.character }}</div> }
              @if (c.episodes) { <div class="text-xs text-slate-500">{{ c.episodes }} {{ c.episodes === 1 ? 'episode' : 'episodes' }}</div> }
            </a>
          }
        </div>
      </section>
    }
  `,
  host: { class: 'block' },
})
export class CreditsView {
  readonly cast = input<CastMember[]>([]);
  readonly crew = input<CrewMember[]>([]);
  readonly loading = input(false);

  protected readonly placeholders = [1, 2, 3, 4, 5, 6, 7, 8];

  protected initials(name: string) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0]).join('').toUpperCase();
  }
}

/** Cast and crew of a library movie or show, fetched from TMDB. */
@Component({
  selector: 'kr-library-credits',
  imports: [CreditsView],
  template: `
    @if (error()) {
      <p class="mb-6 text-sm text-rose-300">{{ error() }}</p>
    } @else {
      <kr-credits [cast]="cast()" [crew]="crew()" [loading]="loading()" />
    }
  `,
  host: { class: 'block mt-8' },
})
export class LibraryCredits {
  private readonly api = inject(Api);

  readonly kind = input.required<'movie' | 'series'>();
  /** Local library id. */
  readonly id = input.required<number>();

  protected readonly cast = signal<CastMember[]>([]);
  protected readonly crew = signal<CrewMember[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const kind = this.kind(), id = this.id();
      untracked(() => this.load(kind, id));
    });
  }

  private async load(kind: 'movie' | 'series', id: number) {
    this.loading.set(true);
    this.error.set(null);
    try {
      const credits = await (kind === 'movie' ? this.api.movieCredits(id) : this.api.seriesCredits(id));
      this.cast.set(credits.cast);
      this.crew.set(credits.crew);
    } catch (e) {
      // A missing TMDB key is reported in Settings; hide the section rather than nag on every detail page.
      const message = errorMessage(e);
      this.cast.set([]);
      this.crew.set([]);
      this.error.set(message.includes('API key') ? null : message);
    } finally {
      this.loading.set(false);
    }
  }
}
