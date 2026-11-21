import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { DiscoverListPage } from './discover-list';

const movie = (tmdbId: number) => ({ tmdbId, title: `Movie ${tmdbId}`, year: 2020, runtime: 0 });

describe('DiscoverListPage', () => {
  it('pages through a list without duplicates', async () => {
    const api = setupTest({
      api: mockApi({
        discoverMovies: async (_list: string, page: number) => page === 1
          ? { results: [movie(1), movie(2)], page: 1, totalPages: 2 }
          : { results: [movie(2), movie(3)], page: 2, totalPages: 2 },
      }),
    });
    const fixture = TestBed.createComponent(DiscoverListPage);
    fixture.componentRef.setInput('media', 'movies');
    fixture.componentRef.setInput('list', 'popular');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.discoverMovies).toHaveBeenCalledWith('popular', 1);
    expect(text(el, 'h1')).toBe('Popular movies');
    expect(el.querySelectorAll('kr-media-card').length).toBe(2);

    button(el, 'Load more').click();
    await settle(fixture);
    expect(api.discoverMovies).toHaveBeenCalledWith('popular', 2);
    expect(el.querySelectorAll('kr-media-card').length).toBe(3);
    expect(text(el)).not.toContain('Load more');
  });

  it('restarts when the list changes', async () => {
    const api = setupTest({
      api: mockApi({
        discoverMovies: async () => ({ results: [movie(1)], page: 1, totalPages: 1 }),
        discoverSeries: async () => ({ results: [{ tmdbId: 9, title: 'Dark', year: 2017 }], page: 1, totalPages: 1 }),
      }),
    });
    const fixture = TestBed.createComponent(DiscoverListPage);
    fixture.componentRef.setInput('media', 'movies');
    fixture.componentRef.setInput('list', 'trending');
    await settle(fixture);
    fixture.componentRef.setInput('media', 'series');
    await settle(fixture);
    expect(api.discoverSeries).toHaveBeenCalledWith('trending', 1);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('kr-media-card').length).toBe(1);
    expect(text(el)).toContain('Dark');
  });

  it('shows errors', async () => {
    setupTest({ api: mockApi({ discoverMovies: async () => { throw new Error('TMDB is unreachable'); } }) });
    const fixture = TestBed.createComponent(DiscoverListPage);
    fixture.componentRef.setInput('media', 'movies');
    fixture.componentRef.setInput('list', 'trending');
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('TMDB is unreachable — open settings');
  });
});
