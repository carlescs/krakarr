import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { BestSubtitleMatches, ScoredSubtitle } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitleBestMatch } from './subtitle-best-match';

const low = {
  candidate: { provider: 'opensubtitles', payload: 'p1', language: 'es', releaseNames: ['Alien.1979.720p.HDTV'], downloads: 3 },
  score: 45, acceptable: true, matches: ['title'], rejections: [],
} as unknown as ScoredSubtitle;

const match = (label: string, results: ScoredSubtitle[]): BestSubtitleMatches =>
  ({ label, missingLanguages: ['es', 'fr'], minimumScore: 70, results, errors: [] });

describe('SubtitleBestMatch', () => {
  it('asks for each file and downloads only what is confirmed', async () => {
    const api = setupTest({
      api: mockApi({
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }, { code: 'fr', name: 'French' }],
        wantedSubtitleLanguages: async () => ({ enabled: true, useEmbeddedSubtitles: true, languages: ['es', 'fr'] }),
        bestSubtitleMatches: async (ref: { movieFileId?: number }) => match(ref.movieFileId === 1 ? 'Alien' : 'Aliens', [low]),
        downloadSubtitle: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitleBestMatch);
    fixture.componentRef.setInput('items', [{ movieFileId: 1, label: 'Alien' }, { movieFileId: 2, label: 'Aliens' }]);
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el)).toContain('Best match · 1 of 2');
    expect(text(el)).toContain('Alien.1979.720p.HDTV');
    expect(text(el)).toContain('below minimum 70');
    expect(text(el)).toMatch(/French\s*No subtitles found/);

    button(el, 'Download 1').click();
    await settle(fixture);
    expect(api.downloadSubtitle).toHaveBeenCalledWith({ movieFileId: 1, episodeFileId: undefined }, low.candidate, 45, false);
    expect(text(el)).toContain('Best match · 2 of 2');

    button(el, 'Skip').click();
    await settle(fixture);
    expect(api.downloadSubtitle).toHaveBeenCalledTimes(1);
    expect(api.bestSubtitleMatches).toHaveBeenCalledTimes(2);
    expect(closed).toHaveBeenCalled();
  });

  it('marks a file not found instead of downloading', async () => {
    const api = setupTest({
      api: mockApi({
        subtitleLanguages: async () => [],
        bestSubtitleMatches: async () => match('Alien', [low]),
        markSubtitlesNotFound: async () => ({ count: 1 }),
      }),
    });
    const fixture = TestBed.createComponent(SubtitleBestMatch);
    fixture.componentRef.setInput('items', [{ episodeFileId: 7, label: 'Alien' }]);
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    await settle(fixture);

    button(fixture.nativeElement, 'Mark not found').click();
    await settle(fixture);
    expect(api.markSubtitlesNotFound).toHaveBeenCalledWith({ movieFileId: undefined, episodeFileId: 7 });
    expect(api.downloadSubtitle).not.toHaveBeenCalled();
    expect(closed).toHaveBeenCalled();
  });

  it('downloads nothing when every language is unticked', async () => {
    setupTest({
      api: mockApi({ subtitleLanguages: async () => [], bestSubtitleMatches: async () => match('Alien', [low]) }),
    });
    const fixture = TestBed.createComponent(SubtitleBestMatch);
    fixture.componentRef.setInput('items', [{ episodeFileId: 7, label: 'Alien' }]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('input[type=checkbox]') as HTMLInputElement).click();
    await settle(fixture);
    expect(button(el, 'Download 0').disabled).toBe(true);
  });
});
