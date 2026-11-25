import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Events } from '../../../core/events';
import { MovieCollectionSummary, MovieFilter } from '../../../core/models';
import { ViewMode } from '../../../core/selection';
import { POSTER_GRID, PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { Poster } from '../../../shared/poster/poster';

/** Delay before reloading after library changes, so a burst of movie updates triggers one request. */
const RELOAD_DELAY_MS = 1000;

/** The movie library as collection cards (or table rows), each opening the collection's page. */
@Component({
  selector: 'kr-collection-grid',
  host: { class: 'block' },
  imports: [RouterLink, Poster, PosterGridSkeleton],
  templateUrl: './collection-grid.html',
  styleUrl: './collection-grid.scss',
})
export class CollectionGrid {
  private readonly api = inject(Api);

  /** Search and filter of the movie list: a collection shows when any of its library movies match. */
  readonly q = input<string>('');
  readonly filter = input<MovieFilter>('all');
  readonly view = input<ViewMode>('grid');

  protected readonly grid = POSTER_GRID;
  protected readonly collections = signal<MovieCollectionSummary[] | null>(null);
  protected readonly error = signal<string | null>(null);
  private requestId = 0;

  constructor() {
    effect(() => {
      const q = this.q().trim();
      const filter = this.filter();
      untracked(() => void this.load(q, filter));
    });

    let timer: ReturnType<typeof setTimeout> | undefined;
    const sub = inject(Events).movieUpdated.subscribe(() => {
      clearTimeout(timer);
      timer = setTimeout(() => void this.load(this.q().trim(), this.filter()), RELOAD_DELAY_MS);
    });
    inject(DestroyRef).onDestroy(() => {
      clearTimeout(timer);
      sub.unsubscribe();
    });
  }

  private async load(q: string, filter: MovieFilter) {
    const id = ++this.requestId;
    try {
      const result = await this.api.movieCollections({ q: q || undefined, filter });
      if (id !== this.requestId) return;
      this.collections.set(result);
      this.error.set(null);
    } catch (e) {
      if (id === this.requestId) this.error.set(errorMessage(e));
    }
  }

  protected percent(c: MovieCollectionSummary): number {
    return c.count ? Math.round((c.downloaded / c.count) * 100) : 0;
  }
}
