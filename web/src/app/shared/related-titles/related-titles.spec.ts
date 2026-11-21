import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { RelatedTitles } from './related-titles';

const movie = (tmdbId: number, title: string) => ({ tmdbId, title, year: 2020, runtime: 0 });

describe('RelatedTitles', () => {
  it('shows recommendations and similar titles of a library movie', async () => {
    const api = setupTest({
      api: mockApi({ movieRecommendations: async () => [movie(1, 'Contact')], similarMovies: async () => [] }),
    });
    const fixture = TestBed.createComponent(RelatedTitles);
    fixture.componentRef.setInput('kind', 'movie');
    fixture.componentRef.setInput('id', 5);
    await settle(fixture);
    expect(api.movieRecommendations).toHaveBeenCalledWith(5);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Recommendations');
    expect(text(el)).toContain('Contact');
    // Empty rows are hidden.
    expect(text(el)).not.toContain('Similar movies');
  });

  it('uses the TMDB endpoints for titles outside the library', async () => {
    const api = setupTest({
      api: mockApi({ tmdbSeriesRecommendations: async () => [], tmdbSimilarSeries: async () => [{ tmdbId: 2, title: 'Dark', year: 2017 }] }),
    });
    const fixture = TestBed.createComponent(RelatedTitles);
    fixture.componentRef.setInput('kind', 'series');
    fixture.componentRef.setInput('id', 70523);
    fixture.componentRef.setInput('source', 'tmdb');
    await settle(fixture);
    expect(api.tmdbSimilarSeries).toHaveBeenCalledWith(70523);
    expect(text(fixture.nativeElement)).toContain('Similar series');
    expect(text(fixture.nativeElement)).toContain('Dark');
  });
});
