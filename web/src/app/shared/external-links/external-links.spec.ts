import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { mockApi, settle, setupTest } from '../../../testing/test-setup';
import { ExternalLinks } from './external-links';

function links(root: HTMLElement) {
  return [...root.querySelectorAll('a')].map(a => ({ label: a.getAttribute('aria-label'), href: a.getAttribute('href'), text: a.textContent!.replace(/\s+/g, ' ').trim() }));
}

describe('ExternalLinks', () => {
  it('links a movie to TMDB, IMDb and Rotten Tomatoes with their scores', async () => {
    const api = setupTest({ api: mockApi({
      ratings: async () => ({ imdb: 8.7, imdbVotes: 2_123_456, rottenTomatoes: 83, metacritic: 73, rottenTomatoesUrl: 'https://www.rottentomatoes.com/m/matrix' }),
    }) });
    const fixture = TestBed.createComponent(ExternalLinks);
    fixture.componentRef.setInput('kind', 'movie');
    fixture.componentRef.setInput('title', 'The Matrix');
    fixture.componentRef.setInput('tmdbId', 603);
    fixture.componentRef.setInput('imdbId', 'tt0133093');
    await settle(fixture);

    expect(api.ratings).toHaveBeenCalledWith('tt0133093');
    expect(links(fixture.nativeElement)).toEqual([
      { label: 'TMDB', href: 'https://www.themoviedb.org/movie/603', text: 'TMDB' },
      { label: 'IMDb', href: 'https://www.imdb.com/title/tt0133093', text: 'IMDb8.72.1M' },
      { label: 'Rotten Tomatoes', href: 'https://www.rottentomatoes.com/m/matrix', text: '83%' },
      { label: 'Metacritic', href: 'https://www.metacritic.com/search/The%20Matrix/?category=movie', text: '73Metacritic' },
    ]);
  });

  it('falls back to a Rotten Tomatoes search without scores, and skips ratings for people', async () => {
    const api = setupTest({ api: mockApi({ ratings: async () => null }) });
    const fixture = TestBed.createComponent(ExternalLinks);
    fixture.componentRef.setInput('kind', 'series');
    fixture.componentRef.setInput('title', 'Breaking Bad');
    fixture.componentRef.setInput('tmdbId', 1396);
    fixture.componentRef.setInput('tvdbId', 81189);
    await settle(fixture);

    expect(api.ratings).not.toHaveBeenCalled();
    expect(links(fixture.nativeElement).map(l => l.href)).toEqual([
      'https://www.themoviedb.org/tv/1396',
      'https://thetvdb.com/dereferrer/series/81189',
      'https://www.rottentomatoes.com/search?search=Breaking%20Bad',
    ]);

    fixture.componentRef.setInput('kind', 'person');
    fixture.componentRef.setInput('imdbId', 'nm0000206');
    await settle(fixture);
    expect(api.ratings).not.toHaveBeenCalled();
    expect(links(fixture.nativeElement).map(l => l.href)).toEqual(['https://www.themoviedb.org/person/1396', 'https://www.imdb.com/name/nm0000206']);
  });
});
