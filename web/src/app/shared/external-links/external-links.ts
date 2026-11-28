import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { Api } from '../../core/api';
import { OmdbInfo } from '../../core/models';

/** For an `episode`, `tmdbId` is the show's TMDB id (with `season`/`episode`) and `tvdbId`/`imdbId` are the episode's own. */
export type ExternalLinkKind = 'movie' | 'series' | 'episode' | 'person';

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
  readonly season = input<number | null | undefined>(null);
  readonly episode = input<number | null | undefined>(null);
  /** OMDb data the page already has (stored on library titles). When set, nothing is fetched. */
  readonly stored = input<OmdbInfo | null | undefined>(null);
  /** OMDb data fetched live (titles outside the library, or not looked up yet), so the page can show its facts too. */
  readonly loaded = output<OmdbInfo>();

  private readonly fetched = signal<OmdbInfo | null>(null);
  protected readonly ratings = computed(() => this.stored() ?? this.fetched());

  protected readonly tmdbUrl = computed(() => {
    const id = this.tmdbId();
    if (!id) return null;
    const kind = this.kind();
    if (kind === 'episode') {
      const season = this.season(), episode = this.episode();
      return season != null && episode != null ? `https://www.themoviedb.org/tv/${id}/season/${season}/episode/${episode}` : null;
    }
    return `https://www.themoviedb.org/${kind === 'series' ? 'tv' : kind}/${id}`;
  });
  protected readonly tvdbUrl = computed(() => {
    const id = this.tvdbId(), kind = this.kind();
    return id && (kind === 'series' || kind === 'episode') ? `https://thetvdb.com/dereferrer/${kind}/${id}` : null;
  });
  protected readonly imdbUrl = computed(() => {
    const id = this.imdbId();
    if (!id) return null;
    return `https://www.imdb.com/${this.kind() === 'person' ? 'name' : 'title'}/${id}`;
  });
  /** The exact Rotten Tomatoes page when OMDb knows it, otherwise a search for the title. */
  protected readonly tomatoesUrl = computed(() => {
    // Rotten Tomatoes has no episode pages worth searching for.
    if (this.kind() === 'person' || this.kind() === 'episode' || !this.title()) return null;
    return this.ratings()?.rottenTomatoesUrl
      ?? `https://www.rottentomatoes.com/search?search=${encodeURIComponent(this.title())}`;
  });
  /** Metacritic only scores movies and seasons, so episodes get no badge. */
  protected readonly showMetacritic = computed(() => this.ratings()?.metacritic != null && this.kind() !== 'person' && this.kind() !== 'episode');
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
      const stored = this.stored();
      untracked(() => this.fetched.set(null));
      if (id && kind !== 'person' && !stored) void this.loadRatings(id);
    });
  }

  private async loadRatings(imdbId: string) {
    try {
      const ratings = await this.api.ratings(imdbId);
      if (imdbId === this.imdbId() && !this.stored()) {
        this.fetched.set(ratings);
        if (ratings) this.loaded.emit(ratings);
      }
    } catch {
      // Ratings are decoration: no OMDb key, unknown title or OMDb unreachable just shows the plain links.
    }
  }

  protected votes(n: number): string {
    return n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M` : n >= 1000 ? `${Math.round(n / 1000)}K` : `${n}`;
  }
}
