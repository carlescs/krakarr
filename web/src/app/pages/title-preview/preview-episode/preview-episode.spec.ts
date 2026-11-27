import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { EpisodeDetails, SeasonEpisode } from '../../../core/models';
import { mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { PreviewEpisodeDialog } from './preview-episode';

const episode: SeasonEpisode = {
  episodeNumber: 3, title: "...And the Bag's in the River", overview: 'Walt cleans up.', airDate: '2008-02-10', runtime: 48,
  stillUrl: 'https://image.tmdb.org/t/p/w300/still.jpg', rating: 8.3,
};

const details: EpisodeDetails = {
  showTmdbId: 1396, tmdbId: 62087, tvdbId: 349234, imdbId: 'tt1054724', runtime: 48, stillUrl: 'https://image.tmdb.org/t/p/w780/still.jpg',
  rating: 8.3, voteCount: 120,
  guestStars: [{ tmdbId: 1, name: 'Max Arciniega', character: 'Krazy-8' }],
  crew: [{ tmdbId: 10, name: 'Adam Bernstein', jobs: 'Director' }],
};

function create(api = mockApi({ tmdbEpisode: async () => details, ratings: async () => null })) {
  setupTest({ api, role: 'User' });
  const fixture = TestBed.createComponent(PreviewEpisodeDialog);
  fixture.componentRef.setInput('tmdbId', 1396);
  fixture.componentRef.setInput('seasonNumber', 1);
  fixture.componentRef.setInput('episode', episode);
  return { api, fixture, root: fixture.nativeElement as HTMLElement };
}

describe('PreviewEpisodeDialog', () => {
  it('shows the episode with its TMDB still, links, guest stars and crew', async () => {
    const { api, fixture, root } = create();
    await settle(fixture);

    expect(api.tmdbEpisode).toHaveBeenCalledWith(1396, 1, 3);
    expect(root.querySelector('[data-still]')?.getAttribute('src')).toBe(details.stillUrl);
    expect(text(root, '[data-meta]')).toContain('Season 1, episode 3');
    expect(text(root, '[data-meta]')).toContain('48 min');
    expect(text(root, '[data-meta]')).toContain('★ 8.3');
    expect(text(root, '[data-overview]')).toBe('Walt cleans up.');
    expect(root.querySelector('a[aria-label="TMDB"]')?.getAttribute('href')).toBe('https://www.themoviedb.org/tv/1396/season/1/episode/3');
    expect(text(root)).toContain('Krazy-8');
    expect(text(root)).toContain('Adam Bernstein');
  });

  it('keeps what the season listing knows when the details fail', async () => {
    const { fixture, root } = create(mockApi({ tmdbEpisode: async () => { throw new Error('Not found on TMDB'); } }));
    await settle(fixture);

    expect(root.querySelector('[data-still]')?.getAttribute('src')).toBe(episode.stillUrl);
    expect(text(root, '[data-overview]')).toBe('Walt cleans up.');
    expect(text(root)).toContain('Not found on TMDB');
  });
});
