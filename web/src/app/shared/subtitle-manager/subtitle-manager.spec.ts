import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { ScoredSubtitle, SubtitleFile } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitleManager } from './subtitle-manager';

const result = {
  candidate: { provider: 'opensubtitles', payload: 'p1', language: 'es', releaseNames: ['Alien.1979.1080p'], fileName: 'a.srt', downloads: 10, hashMatch: true },
  score: 90, acceptable: true, matches: ['hash'], rejections: [],
} as unknown as ScoredSubtitle;

describe('SubtitleManager', () => {
  it('searches providers and downloads a result', async () => {
    const api = setupTest({
      api: mockApi({
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        subtitleCandidates: async () => ({ results: [result], errors: ['Podnapisi timed out'] }),
        downloadSubtitle: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { movieFileId: 4 });
    fixture.componentRef.setInput('label', 'Alien');
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('No subtitles yet.');

    button(el, 'Search providers').click();
    await settle(fixture);
    expect(api.subtitleCandidates).toHaveBeenCalledWith({ movieFileId: 4 }, undefined);
    expect(text(el)).toContain('Alien.1979.1080p');
    expect(text(el)).toContain('hash match');
    expect(text(el)).toContain('Podnapisi timed out');

    button(el, 'Download').click();
    await settle(fixture);
    expect(api.downloadSubtitle).toHaveBeenCalledWith({ movieFileId: 4 }, result.candidate, 90);
    expect(changed).toHaveBeenCalled();
  });

  it('lists and deletes existing subtitles', async () => {
    const api = setupTest({ api: mockApi({ subtitleLanguages: async () => [], deleteSubtitle: async () => undefined }) });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { episodeFileId: 1 });
    fixture.componentRef.setInput('files', [{ id: 12, languageName: 'English', forced: true, provider: 'x', fileName: 'e.srt' } as SubtitleFile]);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('English (forced)');
    button(fixture.nativeElement, 'Delete subtitle').click();
    await settle(fixture);
    expect(api.deleteSubtitle).toHaveBeenCalledWith(12);
  });
});
