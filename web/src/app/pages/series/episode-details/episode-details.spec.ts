import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { Episode, EpisodeDetails } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { EpisodeDetailsDialog } from './episode-details';

const episode: Episode = {
  id: 7, seasonNumber: 1, episodeNumber: 3, title: "...And the Bag's in the River", overview: 'Walt cleans up.', airDate: '2008-02-10', runtime: 48,
  monitored: true, hasAired: true,
  episodeFile: { id: 70, relativePath: 'Season 01/Breaking Bad - S01E03.mkv', size: 1_500_000_000, quality: 'Bluray-1080p', releaseGroup: 'GRP', dateAdded: '2024-01-02T00:00:00Z' },
};

const details: EpisodeDetails = {
  showTmdbId: 1396, tmdbId: 62087, tvdbId: 349234, imdbId: 'tt1054724', runtime: 48, stillUrl: 'https://image.tmdb.org/t/p/w780/still.jpg',
  rating: 8.3, voteCount: 120,
  guestStars: [{ tmdbId: 1, name: 'Max Arciniega', character: 'Krazy-8' }],
  crew: [{ tmdbId: 10, name: 'Adam Bernstein', jobs: 'Director' }],
};

function create(api = mockApi({
  episodeDetails: async () => details, ratings: async () => null, subtitleLanguages: async () => [{ code: 'en', name: 'English' }, { code: 'es', name: 'Spanish' }],
  wantedSubtitleLanguages: async () => ({ enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es'] }),
}), role: 'Admin' | 'User' = 'Admin') {
  setupTest({ api, role });
  const fixture = TestBed.createComponent(EpisodeDetailsDialog);
  fixture.componentRef.setInput('seriesId', 5);
  fixture.componentRef.setInput('episode', episode);
  fixture.componentRef.setInput('subtitles', [{ id: 1, episodeFileId: 70, language: 'en', languageName: 'English', hearingImpaired: false }]);
  return { api, fixture, root: fixture.nativeElement as HTMLElement };
}

describe('EpisodeDetailsDialog', () => {
  it('shows the library data together with the TMDB still, score, links, guest stars and crew', async () => {
    const { api, fixture, root } = create();
    await settle(fixture);

    expect(api.episodeDetails).toHaveBeenCalledWith(5, 7);
    expect(root.querySelector('[data-still]')?.getAttribute('src')).toBe(details.stillUrl);
    expect(text(root, '[data-meta]')).toContain('48 min');
    expect(text(root, '[data-meta]')).toContain('★ 8.3');
    expect(text(root, '[data-overview]')).toBe('Walt cleans up.');
    expect(text(root, '[data-file]')).toContain('Breaking Bad - S01E03.mkv');
    expect(text(root, '[data-file]')).toContain('GRP');
    expect(text(root, '[data-subtitles]')).toContain('English');
    expect(text(root, '[data-subtitles]')).toContain('Spanish · missing');
    expect(root.querySelector('a[aria-label="TMDB"]')?.getAttribute('href')).toBe('https://www.themoviedb.org/tv/1396/season/1/episode/3');
    expect(text(root)).toContain('Guest stars');
    expect(text(root)).toContain('Krazy-8');
    expect(text(root)).toContain('Adam Bernstein');
  });

  it('still shows the library data when TMDB has no such episode', async () => {
    const { fixture, root } = create(mockApi({ episodeDetails: async () => null }));
    await settle(fixture);

    expect(root.querySelector('[data-still]')).toBeNull();
    expect(root.querySelector('kr-external-links')).toBeNull();
    expect(text(root, '[data-overview]')).toBe('Walt cleans up.');
    expect(text(root)).not.toContain('Guest stars');
  });

  it('offers searches and subtitles to staff only', async () => {
    const { fixture, root } = create();
    await settle(fixture);
    const interactive = vi.fn();
    fixture.componentInstance.interactive.subscribe(interactive);
    button(root, 'Interactive search').click();
    expect(interactive).toHaveBeenCalled();
    expect(button(root, 'Subtitles')).toBeTruthy();
    const deleteFile = vi.fn();
    fixture.componentInstance.deleteFile.subscribe(deleteFile);
    button(root, 'Delete file').click();
    expect(deleteFile).toHaveBeenCalled();

    TestBed.resetTestingModule();
    const user = create(undefined, 'User');
    await settle(user.fixture);
    expect(() => button(user.root, 'Automatic search')).toThrow();
    expect(() => button(user.root, 'Delete file')).toThrow();
  });
});
