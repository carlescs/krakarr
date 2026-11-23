import { Injectable, effect, inject } from '@angular/core';
import { Api, errorMessage } from './api';
import { Auth } from './auth';
import { Events } from './events';
import {
  MovieListItem,
  MovieListPage,
  MovieListQuery,
  SeriesListItem,
  SeriesListPage,
  SeriesListQuery,
} from './models';
import { PagedListCache } from './paged-list';
import { Toasts } from './toast';

export type MovieQuery = Omit<MovieListQuery, 'page' | 'pageSize'>;
export type SeriesQuery = Omit<SeriesListQuery, 'page' | 'pageSize'>;

/** The paginated movie and series lists shared by the library pages. */
@Injectable({ providedIn: 'root' })
export class Library {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly auth = inject(Auth);

  readonly movies = new PagedListCache<MovieListItem, MovieQuery, MovieListPage>(
    { filter: 'all', sort: 'title', groupBy: 'none' },
    (q, page, pageSize) => this.api.movies({ ...q, page, pageSize }),
    e => this.toasts.error(errorMessage(e)),
  );

  readonly series = new PagedListCache<SeriesListItem, SeriesQuery, SeriesListPage>(
    { filter: 'all', sort: 'title' },
    (q, page, pageSize) => this.api.seriesList({ ...q, page, pageSize }),
    e => this.toasts.error(errorMessage(e)),
  );

  constructor() {
    const events = inject(Events);
    events.movieUpdated.subscribe(() => this.movies.invalidate());
    events.seriesUpdated.subscribe(() => this.series.invalidate());
    // Another user may log in next: never show them the previous session's library.
    effect(() => {
      if (this.auth.status()?.authenticated === false) {
        this.movies.clear();
        this.series.clear();
      }
    });
  }
}
