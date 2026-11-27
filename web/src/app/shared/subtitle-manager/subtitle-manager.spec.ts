import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { ScoredSubtitle, SubtitleFile } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitleManager } from './subtitle-manager';

const result = (language: string, payload: string, score: number, best = false) => ({
  candidate: { provider: 'opensubtitles', payload, language, releaseNames: [`Alien.1979.${payload}`], fileName: 'a.srt', downloads: 10, hashMatch: score > 80 },
  score, acceptable: score >= 70, best, matches: ['hash'], rejections: score >= 70 ? [] : [`score ${score} below minimum 70`],
}) as unknown as ScoredSubtitle;

const languages = {
  subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }, { code: 'it', name: 'Italian' }, { code: 'en', name: 'English' }],
  wantedSubtitleLanguages: async () => ({ enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es', 'it'] }),
};
const english = { id: 5, language: 'en', languageName: 'English', forced: false, provider: 'disk', fileName: 'a.en.srt' } as SubtitleFile;

describe('SubtitleManager', () => {
  it('loads the file\'s subtitles and searches the missing languages right away', async () => {
    const api = setupTest({
      api: mockApi({
        ...languages,
        subtitleFiles: async () => [english],
        subtitleCandidates: async () => ({ label: 'Alien', results: [result('es', 'good', 90, true), result('it', 'low', 40, true), result('es', 'other', 50)], errors: ['Podnapisi timed out'], minimumScore: 70 }),
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { movieFileId: 4 });
    fixture.componentRef.setInput('label', 'Alien');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(api.subtitleFiles).toHaveBeenCalledWith({ movieFileId: 4 });
    expect(text(el, '[data-current]')).toContain('English');
    expect(text(el, '[data-current]')).toContain('Spanish · missing');
    expect(api.subtitleCandidates).toHaveBeenCalledWith({ movieFileId: 4 }, ['es', 'it']);
    expect(text(el)).toContain('Podnapisi timed out');
    expect(el.querySelectorAll('[data-result]').length).toBe(3);
    expect(el.querySelectorAll('[data-top-pick]').length).toBe(2);
    expect(text(el, '[data-top-picks]')).toContain('Top pick found for Spanish, Italian');
    expect(text(el, '[data-top-picks]')).toContain('minimum score of 70');

    // Results can be narrowed to one of the searched languages.
    button(el, 'Italian 1').click();
    await settle(fixture);
    expect(el.querySelectorAll('[data-result]').length).toBe(1);
  });

  it('downloads a result picked by hand and shows it as downloaded', async () => {
    let files: SubtitleFile[] = [english];
    const pick = result('es', 'good', 90, true);
    const api = setupTest({
      api: mockApi({
        ...languages,
        subtitleFiles: async () => files,
        subtitleCandidates: async () => ({ label: 'Alien', results: [pick], errors: [] }),
        downloadSubtitle: async () => {
          files = [...files, { id: 6, language: 'es', languageName: 'Spanish', forced: false, provider: 'opensubtitles', fileName: 'a.es.srt' } as SubtitleFile];
          return files[1];
        },
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { movieFileId: 4 });
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Download Alien.1979.good').click();
    await settle(fixture);
    expect(api.downloadSubtitle).toHaveBeenCalledWith({ movieFileId: 4 }, pick.candidate, 90, true);
    expect(changed).toHaveBeenCalled();
    expect(el.querySelector('[data-downloaded]')).not.toBeNull();
    expect(text(el, '[data-current]')).not.toContain('Spanish · missing');
    // Spanish is no longer missing, so its top pick is not offered again.
    expect(text(el, '[data-top-picks]')).not.toContain('Spanish');
  });

  it('downloads every top pick in one go, letting upgrades replace them later', async () => {
    const es = result('es', 'good', 90, true);
    const it = result('it', 'low', 40, true);
    const api = setupTest({
      api: mockApi({
        ...languages,
        subtitleFiles: async () => [english],
        subtitleCandidates: async () => ({ label: 'Alien', results: [es, it, result('es', 'other', 50)], errors: [] }),
        downloadSubtitle: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { episodeFileId: 9 });
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Download 2 top picks').click();
    await settle(fixture);
    expect(api.downloadSubtitle).toHaveBeenCalledTimes(2);
    expect(api.downloadSubtitle).toHaveBeenCalledWith({ episodeFileId: 9 }, es.candidate, 90, false);
    expect(api.downloadSubtitle).toHaveBeenCalledWith({ episodeFileId: 9 }, it.candidate, 40, false);
    expect(el.querySelector('[data-top-picks]')).toBeNull();
  });

  it('searches the language it was opened for, and another one when a missing language is clicked', async () => {
    const api = setupTest({
      api: mockApi({
        ...languages,
        subtitleFiles: async () => [english],
        subtitleCandidates: async () => ({ label: 'Alien', results: [], errors: [] }),
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { movieFileId: 4 });
    fixture.componentRef.setInput('language', 'it');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.subtitleCandidates).toHaveBeenLastCalledWith({ movieFileId: 4 }, 'it');
    expect(text(el)).toContain('No subtitles found');

    button(el, 'Spanish · missing').click();
    await settle(fixture);
    expect(api.subtitleCandidates).toHaveBeenLastCalledWith({ movieFileId: 4 }, 'es');
  });

  it('searches in the background or stops the automatic search for the file', async () => {
    const api = setupTest({
      api: mockApi({
        ...languages,
        subtitleFiles: async () => [],
        subtitleCandidates: async () => ({ label: 'Alien', results: [], errors: [] }),
        searchSubtitles: async () => undefined,
        markSubtitlesNotFound: async () => ({ count: 1 }),
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { movieFileId: 4 });
    const closed = vi.fn();
    fixture.componentInstance.closed.subscribe(closed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Search in background').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenCalledWith({ movieFileId: 4 });

    button(el, 'Stop searching automatically').click();
    await settle(fixture);
    expect(api.markSubtitlesNotFound).toHaveBeenCalledWith({ movieFileId: 4 });
    expect(closed).toHaveBeenCalled();
  });

  it('lists and deletes existing subtitles, but not tracks inside the video', async () => {
    let files = [
      { id: 12, language: 'en', languageName: 'English', forced: true, provider: 'x', fileName: 'e.srt' } as SubtitleFile,
      { id: 0, language: 'es', languageName: 'Spanish', forced: false, provider: 'embedded', fileName: 'Alien.mkv' } as SubtitleFile,
    ];
    const api = setupTest({
      api: mockApi({
        subtitleLanguages: async () => [],
        wantedSubtitleLanguages: async () => ({ enabled: false, useEmbeddedSubtitles: true, languages: [] }),
        subtitleFiles: async () => files,
        deleteSubtitle: async () => { files = files.filter(f => f.id !== 12); },
      }),
    });
    const fixture = TestBed.createComponent(SubtitleManager);
    fixture.componentRef.setInput('fileRef', { episodeFileId: 1 });
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const rows = () => [...el.querySelectorAll('[data-subtitle]')].map(r => text(r as HTMLElement));
    expect(rows()).toEqual([expect.stringContaining('English (forced)'), 'SpanishEmbeddedAlien.mkv']);
    expect(el.querySelectorAll('[aria-label="Delete subtitle"]').length).toBe(1);
    // Nothing wanted: no automatic search, the language has to be picked.
    expect(api.subtitleCandidates).not.toHaveBeenCalled();
    expect(text(el)).toContain('Pick a language to search the providers');

    button(el, 'Delete subtitle').click();
    await settle(fixture);
    expect(api.deleteSubtitle).toHaveBeenCalledWith(12);
    expect(rows()).toEqual(['SpanishEmbeddedAlien.mkv']);
  });
});
