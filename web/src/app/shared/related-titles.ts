import { Component, WritableSignal, effect, inject, input, signal, untracked } from '@angular/core';
import { Api, errorMessage } from '../core/api';
import { movieItems, seriesItems } from '../core/discover';
import { DiscoverItem } from '../core/models';
import { MediaRow } from './media-row';

interface RelatedState { items: DiscoverItem[]; loading: boolean; error: string | null; }
const LOADING: RelatedState = { items: [], loading: true, error: null };

/** "Recommendations" and "Similar" rows for a library movie or show, from TMDB. */
@Component({
  selector: 'kr-related-titles',
  imports: [MediaRow],
  template: `
    @if (recommendations().loading || recommendations().items.length > 0 || recommendations().error) {
      <kr-media-row title="Recommendations" [items]="recommendations().items" [loading]="recommendations().loading" [error]="recommendations().error" />
    }
    @if (similar().loading || similar().items.length > 0 || similar().error) {
      <kr-media-row [title]="kind() === 'movie' ? 'Similar movies' : 'Similar series'" [items]="similar().items" [loading]="similar().loading" [error]="similar().error" />
    }
  `,
  host: { class: 'block mt-8' },
})
export class RelatedTitles {
  private readonly api = inject(Api);

  readonly kind = input.required<'movie' | 'series'>();
  /** Local library id. */
  readonly id = input.required<number>();

  protected readonly recommendations = signal<RelatedState>(LOADING);
  protected readonly similar = signal<RelatedState>(LOADING);

  constructor() {
    effect(() => {
      const kind = this.kind(), id = this.id();
      untracked(() => this.load(kind, id));
    });
  }

  private load(kind: 'movie' | 'series', id: number) {
    this.recommendations.set(LOADING);
    this.similar.set(LOADING);
    this.fetch(this.recommendations, kind === 'movie'
      ? this.api.movieRecommendations(id).then(movieItems)
      : this.api.seriesRecommendations(id).then(seriesItems));
    this.fetch(this.similar, kind === 'movie'
      ? this.api.similarMovies(id).then(movieItems)
      : this.api.similarSeries(id).then(seriesItems));
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
