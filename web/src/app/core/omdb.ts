import { computed, signal } from '@angular/core';
import { OmdbInfo } from './models';

/** A title a page shows: its IMDb id and, for library titles, the OMDb data stored with it. */
export interface OmdbSource { imdbId?: string | null; omdb?: OmdbInfo | null; }

/**
 * OMDb data for a detail page: the copy stored with a library title when it has one, otherwise what `kr-external-links`
 * fetched live for the same IMDb id (wire its `(loaded)` to {@link loaded}). Fetched data of a previous title never leaks
 * into the next one.
 */
export class OmdbState {
  private readonly live = signal<{ imdbId: string; info: OmdbInfo } | null>(null);

  constructor(private readonly source: () => OmdbSource | null | undefined) {}

  readonly info = computed<OmdbInfo | null>(() => {
    const title = this.source();
    if (!title) return null;
    if (title.omdb) return title.omdb;
    const live = this.live();
    return live && live.imdbId === title.imdbId ? live.info : null;
  });

  loaded(info: OmdbInfo) {
    const imdbId = this.source()?.imdbId;
    if (imdbId) this.live.set({ imdbId, info });
  }
}
