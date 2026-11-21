import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { WantedSubtitle } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitlesPage } from './subtitles';

const wanted: WantedSubtitle[] = [
  { mediaType: 'Movie', movieId: 3, movieFileId: 30, label: 'Alien (1979)', missingLanguages: ['es'] },
  { mediaType: 'Series', seriesId: 4, episodeFileId: 40, label: 'Dark S01E01', missingLanguages: ['en', 'es'] },
];
const settings = { enabled: true, openSubtitles: { apiKey: 'key' }, languages: ['es'], searchIntervalHours: 6 };

describe('SubtitlesPage', () => {
  it('lists files missing subtitles and searches them', async () => {
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        wantedSubtitles: async () => wanted,
        searchSubtitles: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.settings).toHaveBeenCalledWith('subtitles');
    expect(text(el)).toContain('Wanted languages: Spanish');
    expect(el.querySelector('a[href="/movies/3"]')?.textContent).toBe('Alien (1979)');
    expect(el.querySelector('a[href="/series/4"]')?.textContent).toBe('Dark S01E01');

    button(el, 'Search').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenCalledWith({ movieFileId: 30, episodeFileId: undefined });

    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith();

    button(el, 'Manual').click();
    await settle(fixture);
    expect(el.querySelector('kr-subtitle-manager')).not.toBeNull();
  });

  it('warns admins when no provider is configured', async () => {
    setupTest({
      api: mockApi({ settings: async () => ({ ...settings, openSubtitles: { apiKey: '' } }), subtitleLanguages: async () => [], wantedSubtitles: async () => wanted }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('enabled but no provider is configured');
    expect(button(fixture.nativeElement, 'Search all missing').disabled).toBe(true);
  });

  it('does not read settings as a manager', async () => {
    const api = setupTest({ role: 'Manager', api: mockApi({ subtitleLanguages: async () => [], wantedSubtitles: async () => [] }) });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    expect(api.settings).not.toHaveBeenCalled();
    expect(text(fixture.nativeElement)).toContain('Every library file has subtitles');
  });
});
