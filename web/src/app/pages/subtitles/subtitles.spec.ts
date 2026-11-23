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

  it('is ready when SubDL is configured without OpenSubtitles', async () => {
    setupTest({
      api: mockApi({
        settings: async () => ({ ...settings, openSubtitles: { apiKey: '' }, subdl: { enabled: true, apiKey: 'subdl-key' } }),
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        wantedSubtitles: async () => wanted,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Wanted languages: Spanish');
    expect(button(fixture.nativeElement, 'Search all missing').disabled).toBe(false);
  });

  it('searches all missing using only the selected subtitle providers', async () => {
    const api = setupTest({
      api: mockApi({
        settings: async () => ({ ...settings, subdl: { enabled: true, apiKey: 'subdl-key' } }),
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        subtitleProviders: async () => [
          { name: 'opensubtitles', displayName: 'OpenSubtitles', configured: true },
          { name: 'subdl', displayName: 'SubDL', configured: true },
          { name: 'disabled-provider', displayName: 'Disabled', configured: false },
        ],
        wantedSubtitles: async () => wanted,
        searchSubtitles: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    // Only configured providers appear in the picker.
    button(el, 'All providers').click();
    await settle(fixture);
    const checkboxes = () => [...el.querySelectorAll<HTMLInputElement>('kr-multi-picker input[type="checkbox"]')];
    expect(checkboxes().length).toBe(2);

    // Selecting one provider passes it to searchSubtitles.
    checkboxes()[1].click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl'] });

    // Selecting a second provider passes both.
    button(el, 'SubDL').click();
    await settle(fixture);
    checkboxes()[0].click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl', 'opensubtitles'] });

    // Clearing the selection falls back to searching all configured providers.
    button(el, '2 providers').click();
    await settle(fixture);
    button(el, 'All providers').click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith();
  });

  it('does not read settings as a manager but can pick subtitle providers', async () => {
    const api = setupTest({
      role: 'Manager',
      api: mockApi({
        subtitleLanguages: async () => [],
        subtitleProviders: async () => [{ name: 'subdl', displayName: 'SubDL', configured: true }],
        wantedSubtitles: async () => wanted,
        searchSubtitles: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.settings).not.toHaveBeenCalled();
    expect(api.subtitleProviders).toHaveBeenCalled();

    button(el, 'All providers').click();
    await settle(fixture);
    el.querySelector<HTMLInputElement>('kr-multi-picker input[type="checkbox"]')!.click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl'] });
  });
});
