import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { UserRole } from '../../core/models';
import { MockApi, button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { TitlePreviewPage } from './title-preview';

const movieDetails = (movieId?: number) => ({
  movie: { tmdbId: 329865, title: 'Arrival', year: 2016, runtime: 116, overview: 'Linguist meets aliens.', imdbId: 'tt2543164', movieId },
  tagline: 'Why are they here?', genres: ['Drama', 'Sci-Fi'], releaseDate: '2016-11-11',
  releaseRegion: 'ES',
  releases: [
    { type: 'Theatrical', region: { date: '2016-11-18', country: 'ES' }, earliest: { date: '2016-11-11', country: 'US' } },
    { type: 'Digital', region: null, earliest: { date: '2017-01-31', country: 'US' } },
    { type: 'Physical', region: null, earliest: null },
  ],
  cast: [{ tmdbId: 1, name: 'Amy Adams' }], crew: [],
});

async function render(kind: 'movie' | 'series', api: MockApi, role: UserRole = 'Admin') {
  setupTest({ role, api });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  const fixture = TestBed.createComponent(TitlePreviewPage);
  fixture.componentRef.setInput('kind', kind);
  fixture.componentRef.setInput('tmdbId', '329865');
  await settle(fixture);
  return { fixture, navigate, el: fixture.nativeElement as HTMLElement };
}

describe('TitlePreviewPage', () => {
  it('shows a movie that is not in the library and offers to add it', async () => {
    const api = mockApi({ tmdbMovie: async () => movieDetails(), requestFor: async () => null });
    const { fixture, el } = await render('movie', api);
    expect(api.tmdbMovie).toHaveBeenCalledWith(329865);
    expect(text(el, 'h1')).toBe('Arrival (2016)');
    expect(text(el)).toContain('Why are they here?');
    expect(text(el)).toContain('116 min');
    expect(text(el)).toContain('Not in library');
    expect(text(el)).toContain('Releases');
    expect(text(el)).toContain('In cinemas');
    expect(text(el)).toContain('Earliest worldwide · no date for ES');
    expect(text(el)).toContain('Unknown');
    expect(text(el)).toContain('Amy Adams');
    expect(el.querySelector('a[href="https://www.imdb.com/title/tt2543164"]')).not.toBeNull();

    button(el, '+ Add to library').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-movie-modal')).not.toBeNull();
  });

  it('redirects to the library page when the title is in the library', async () => {
    const { navigate } = await render('movie', mockApi({ tmdbMovie: async () => movieDetails(12) }));
    expect(navigate).toHaveBeenCalledWith(['/movies', 12], { replaceUrl: true });
  });

  it('shows seasons of a show and an open request to plain users', async () => {
    const api = mockApi({
      tmdbSeries: async () => ({
        show: { tmdbId: 329865, tvdbId: 7, title: 'Dark', year: 2017, network: 'Netflix', status: 'Ended' }, genres: [], cast: [], crew: [], episodeCount: 26,
        seasons: [{ seasonNumber: 0, episodeCount: 1 }, { seasonNumber: 1, name: 'Season 1', episodeCount: 10, airDate: '2017-12-01' }],
      }),
      requestFor: async () => ({ id: 1, requestedBy: 'kim' }),
    });
    const { el } = await render('series', api, 'User');
    expect(text(el)).toContain('Netflix');
    expect(text(el)).toContain('26 episodes');
    expect(text(el)).toContain('Specials');
    expect(text(el)).toContain('Season 1');
    expect(text(el)).toContain('Requested by kim');
    expect(el.querySelector('a[href="/requests"]')?.textContent).toContain('View requests');
  });

  it('shows errors', async () => {
    const { el } = await render('movie', mockApi({ tmdbMovie: async () => { throw new Error('Not found on TMDB'); } }));
    expect(text(el)).toContain('Not found on TMDB');
  });
});
