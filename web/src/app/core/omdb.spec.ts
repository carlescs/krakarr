import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { OmdbInfo } from './models';
import { OmdbSource, OmdbState } from './omdb';

const info = (imdb: number): OmdbInfo => ({ imdb, imdbVotes: null, rottenTomatoes: null, metacritic: null, rottenTomatoesUrl: null });

describe('OmdbState', () => {
  it('prefers stored data and keeps fetched data to the title it was fetched for', () => {
    const title = signal<OmdbSource | null>({ imdbId: 'tt1', omdb: null });
    const state = new OmdbState(() => title());
    expect(state.info()).toBeNull();

    state.loaded(info(7));
    expect(state.info()?.imdb).toBe(7);

    // Another title: what was fetched for the first one doesn't apply.
    title.set({ imdbId: 'tt2', omdb: null });
    expect(state.info()).toBeNull();

    title.set({ imdbId: 'tt2', omdb: info(9) });
    expect(state.info()?.imdb).toBe(9);
  });
});
