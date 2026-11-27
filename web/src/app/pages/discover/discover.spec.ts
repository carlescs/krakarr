import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { DiscoverPage } from './discover';

describe('DiscoverPage', () => {
  it('shows personal picks and the TMDB lists, movies and series interleaved', async () => {
    const api = setupTest({
      role: 'User',
      api: mockApi({
        moviesForYou: async () => [{ tmdbId: 1, title: 'Arrival', year: 2016, runtime: 0 }],
        seriesForYou: async () => { throw new Error('nope'); },
        discoverMovies: async () => ({ results: [{ tmdbId: 2, title: 'Dune', year: 2021, runtime: 0 }], page: 1, totalPages: 1 }),
        discoverSeries: async () => ({ results: [], page: 1, totalPages: 1 }),
      }),
    });
    const fixture = TestBed.createComponent(DiscoverPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.discoverMovies).toHaveBeenCalledWith('trending');
    expect(api.discoverSeries).toHaveBeenCalledWith('trending');
    const rows = [...el.querySelectorAll('kr-media-row h2')].map(h => h.textContent);
    expect(rows[0]).toBe('Recommended for you');
    expect(rows.length).toBe(9);
    expect(text(el)).toContain('Arrival');
    expect(text(el)).toContain('Dune');
    // Plain users can search too, and get a link to their requests.
    expect(el.querySelector('a[href="/requests"]')).not.toBeNull();
    expect(el.querySelector('a[href="/discover/search"]')).not.toBeNull();
  });

  it('explains a missing TMDB key instead of showing empty rows', async () => {
    setupTest({
      api: mockApi({
        moviesForYou: async () => [],
        seriesForYou: async () => [],
        discoverMovies: async () => { throw new Error('No TMDB API key configured'); },
        discoverSeries: async () => { throw new Error('No TMDB API key configured'); },
      }),
    });
    const fixture = TestBed.createComponent(DiscoverPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('No TMDB API key configured');
    expect(el.querySelector('a[href="/settings"]')).not.toBeNull();
    expect(el.querySelector('kr-media-row')).toBeNull();
  });
});
