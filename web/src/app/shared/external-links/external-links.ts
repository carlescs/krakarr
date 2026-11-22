import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Api } from '../../core/api';
import { TitleRatings } from '../../core/models';

export type ExternalLinkKind = 'movie' | 'series' | 'person';

/** Logo links to a title's (or person's) pages on TMDB, TheTVDB, IMDb and Rotten Tomatoes, with their scores when OMDb is set up. */
@Component({
  selector: 'kr-external-links',
  templateUrl: './external-links.html',
  styleUrl: './external-links.scss',
  host: { class: 'flex flex-wrap items-center gap-2' },
})
export class ExternalLinks {
  private readonly api = inject(Api);

  readonly kind = input.required<ExternalLinkKind>();
  readonly title = input<string>('');
  readonly tmdbId = input<number | null | undefined>(null);
  readonly tvdbId = input<number | null | undefined>(null);
  readonly imdbId = input<string | null | undefined>(null);

  protected readonly ratings = signal<TitleRatings | null>(null);

  protected readonly tmdbUrl = computed(() => {
    const id = this.tmdbId();
    if (!id) return null;
    const path = this.kind() === 'series' ? 'tv' : this.kind();
    return `https://www.themoviedb.org/${path}/${id}`;
  });
  protected readonly tvdbUrl = computed(() => this.kind() === 'series' && this.tvdbId() ? `https://thetvdb.com/dereferrer/series/${this.tvdbId()}` : null);
  protected readonly imdbUrl = computed(() => {
    const id = this.imdbId();
    if (!id) return null;
    return `https://www.imdb.com/${this.kind() === 'person' ? 'name' : 'title'}/${id}`;
  });
  /** The exact Rotten Tomatoes page when OMDb knows it, otherwise a search for the title. */
  protected readonly tomatoesUrl = computed(() => {
    if (this.kind() === 'person' || !this.title()) return null;
    return this.ratings()?.rottenTomatoesUrl
      ?? `https://www.rottentomatoes.com/search?search=${encodeURIComponent(this.title())}`;
  });
  /** Metacritic search for the title. Its search filter takes numeric categories: 2 = movies, 1 = TV shows. */
  protected readonly metacriticUrl = computed(() =>
    `https://www.metacritic.com/search/${encodeURIComponent(this.title())}/?page=1&category=${this.kind() === 'series' ? 1 : 2}`);
  protected readonly fresh = computed(() => (this.ratings()?.rottenTomatoes ?? 0) >= 60);
  protected readonly metacriticClass = computed(() => {
    const score = this.ratings()?.metacritic ?? 0;
    return score >= 61 ? 'bg-[#00ce7a] text-black' : score >= 40 ? 'bg-[#ffbd3f] text-black' : 'bg-[#ff6874] text-black';
  });

  constructor() {
    effect(() => {
      const id = this.imdbId();
      const kind = this.kind();
      this.ratings.set(null);
      if (id && kind !== 'person') void this.loadRatings(id);
    });
  }

  private async loadRatings(imdbId: string) {
    try {
      const ratings = await this.api.ratings(imdbId);
      if (imdbId === this.imdbId()) this.ratings.set(ratings);
    } catch {
      // Ratings are decoration: no OMDb key, unknown title or OMDb unreachable just shows the plain links.
    }
  }

  protected votes(n: number): string {
    return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}K` : `${n}`;
  }
}
