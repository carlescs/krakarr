import { Injectable, effect, inject, signal } from '@angular/core';
import { Api, errorMessage } from './api';
import { Auth } from './auth';
import { Events } from './events';
import { MovieCollection, MovieListItem, SeriesListItem } from './models';
import { Toasts } from './toast';

/**
 * A library list that outlives the page showing it: returning to the page shows the last list straight away and refreshes
 * it in the background, and bursts of update events (imports, refreshes) collapse into one reload per `delayMs`.
 */
export class ListCache<T> {
  /** The last list loaded, or null before the first load finishes. */
  readonly items = signal<T[] | null>(null);

  private watchers = 0;
  private loading?: Promise<void>;
  private queued = false;
  private timer?: ReturnType<typeof setTimeout>;

  constructor(
    private readonly fetch: () => Promise<T[]>,
    private readonly onError: (e: unknown) => void,
    private readonly delayMs = 1000,
  ) {}

  /** A page started showing the list: refresh it, and keep it fresh until the returned function is called. */
  attach(): () => void {
    this.watchers++;
    void this.refresh();
    let attached = true;
    return () => {
      if (!attached) return;
      attached = false;
      this.watchers--;
    };
  }

  /** Something in the list changed. Reloads once after `delayMs` if a page shows it; otherwise the next attach reloads. */
  invalidate() {
    if (this.watchers === 0 || this.timer) return;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.refresh();
    }, this.delayMs);
  }

  /** Drops the cached list (e.g. on logout). */
  clear() {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.items.set(null);
  }

  private refresh(): Promise<void> {
    if (this.loading) {
      // A change arrived while loading: the running request may predate it, so load once more afterwards.
      this.queued = true;
      return this.loading;
    }
    this.loading = (async () => {
      try {
        this.items.set(await this.fetch());
      } catch (e) {
        if (this.items() === null) this.items.set([]);
        this.onError(e);
      } finally {
        this.loading = undefined;
        if (this.queued) {
          this.queued = false;
          if (this.watchers > 0) void this.refresh();
        }
      }
    })();
    return this.loading;
  }
}

/** The movie and series lists shared by the library pages. */
@Injectable({ providedIn: 'root' })
export class Library {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly auth = inject(Auth);

  readonly movies = new ListCache<MovieListItem>(() => this.api.movies(), e => this.toasts.error(errorMessage(e)));
  /** TMDB collections of library movies, for grouping the movie list. Movie events can change them too. */
  readonly movieCollections = new ListCache<MovieCollection>(() => this.api.movieCollections(), e => this.toasts.error(errorMessage(e)));
  readonly series = new ListCache<SeriesListItem>(() => this.api.seriesList(), e => this.toasts.error(errorMessage(e)));

  constructor() {
    const events = inject(Events);
    events.movieUpdated.subscribe(() => {
      this.movies.invalidate();
      this.movieCollections.invalidate();
    });
    events.seriesUpdated.subscribe(() => this.series.invalidate());
    // Another user may log in next: never show them the previous session's library.
    effect(() => {
      if (this.auth.status()?.authenticated === false) {
        this.movies.clear();
        this.movieCollections.clear();
        this.series.clear();
      }
    });
  }
}
