import { describe, expect, it } from 'vitest';
import { SubtitleFile } from './models';
import { missingSubtitleLanguages, subtitleLabel, subtitleSource } from './subtitles';

const sub = (language: string, extra: Partial<SubtitleFile> = {}) =>
  ({ id: 1, language, languageName: language, hearingImpaired: false, forced: false, provider: 'opensubtitles', ...extra }) as SubtitleFile;

describe('subtitles', () => {
  it('lists wanted languages without a full subtitle', () => {
    const wanted = { enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es', 'fr'] };
    const files = [sub('en'), sub('es', { forced: true }), sub('fr', { id: 0, provider: 'embedded' })];
    expect(missingSubtitleLanguages(files, wanted)).toEqual(['es']);
    expect(missingSubtitleLanguages(files, { ...wanted, useEmbeddedSubtitles: false })).toEqual(['es', 'fr']);
    expect(missingSubtitleLanguages(files, { ...wanted, enabled: false })).toEqual([]);
    expect(missingSubtitleLanguages(files, null)).toEqual([]);
  });

  it('labels subtitles and their source', () => {
    expect(subtitleLabel(sub('en', { languageName: 'English', hearingImpaired: true, forced: true }))).toBe('English (HI, forced)');
    expect(subtitleSource(sub('en', { provider: 'embedded' }))).toBe('Inside the video file');
    expect(subtitleSource(sub('en', { provider: 'disk' }))).toBe('On disk');
  });
});
