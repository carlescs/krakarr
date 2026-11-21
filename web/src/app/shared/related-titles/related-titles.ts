import { Component, WritableSignal, effect, inject, input, signal, untracked } from '@angular/core';
import { Api, errorMessage } from '../../core/api';
import { movieItems, seriesItems } from '../../core/discover';
import { DiscoverItem } from '../../core/models';
import { MediaRow } from '../media-row/media-row';

interface RelatedState { items: DiscoverItem[]; loading: boolean; error: string | null; }
const LOADING: RelatedState = { items: [], loading: true, error: null };

/** "Recommendations" and "Similar" rows for a movie or show, from TMDB. */
@Component({
  selector: 'kr-related-titles',
  imports: [MediaRow],
  templateUrl: './related-titles.html',
  styleUrl: './related-titles.scss',
  host: { class: 'block mt-8' },
})
export class RelatedTitles {
  private readonly api = inject(Api);

  readonly kind = input.required<'movie' | 'series'>();
  /** Local library id, or the TMDB id when source is 'tmdb'. */
  readonly id = input.required<number>();
  readonly source = input<'library' | 'tmdb'>('library');

  protected readonly recommendations = signal<RelatedState>(LOADING);
  protected readonly similar = signal<RelatedState>(LOADING);

  constructor() {
    effect(() => {
      const kind = this.kind(), id = this.id(), source = this.source();
      untracked(() => this.load(kind, id, source));
    });
  }

  private load(kind: 'movie' | 'series', id: number, source: 'library' | 'tmdb') {
    this.recommendations.set(LOADING);
    this.similar.set(LOADING);
    const tmdb = source === 'tmdb';
    this.fetch(this.recommendations, kind === 'movie'
      ? (tmdb ? this.api.tmdbMovieRecommendations(id) : this.api.movieRecommendations(id)).then(movieItems)
      : (tmdb ? this.api.tmdbSeriesRecommendations(id) : this.api.seriesRecommendations(id)).then(seriesItems));
    this.fetch(this.similar, kind === 'movie'
      ? (tmdb ? this.api.tmdbSimilarMovies(id) : this.api.similarMovies(id)).then(movieItems)
      : (tmdb ? this.api.tmdbSimilarSeries(id) : this.api.similarSeries(id)).then(seriesItems));
  }

  private async fetch(target: WritableSignal<RelatedState>, request: Promise<DiscoverItem[]>) {
    try {
      target.set({ items: await request, loading: false, error: null });
    } catch (e) {
      // A missing TMDB key is reported in Settings; hide the rows rather than nag on every detail page.
      const message = errorMessage(e);
      target.set({ items: [], loading: false, error: message.includes('API key') ? null : message });
    }
  }
}
