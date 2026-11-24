import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { WantedSubtitle } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { Events } from '../../core/events';
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
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ notFound: false });

    button(el, 'Manual').click();
    await settle(fixture);
    expect(el.querySelector('kr-subtitle-manager')).not.toBeNull();
  });

  it('hides movies marked not found by default, counts each filter and retries them', async () => {
    const notFound: WantedSubtitle = { ...wanted[0], notFound: '2026-11-20T10:00:00Z' };
    let marked = true;
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        wantedSubtitles: async (onlyNotFound?: boolean) => {
          const all = marked ? [notFound, wanted[1]] : [wanted[0], wanted[1]];
          return onlyNotFound == null ? all : all.filter(w => !!w.notFound === onlyNotFound);
        },
        wantedSubtitleCounts: async () => marked ? { searching: 250, notFound: 1 } : { searching: 251, notFound: 0 },
        retrySubtitles: async () => { marked = false; return { count: 1 }; },
        searchSubtitles: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const radio = (label: string) => [...el.querySelectorAll<HTMLButtonElement>('[role="radio"]')].find(b => b.textContent!.trim().startsWith(label))!;
    const radioText = () => [...el.querySelectorAll('[role="radio"]')].map(b => b.textContent!.replace(/\s+/g, ' ').trim());
    const heading = () => el.querySelector('h2')!.textContent!.replace(/\s+/g, ' ').trim();

    // Default: only files still searched automatically. Counts come from the server, not the (capped) list.
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith(false);
    expect(radio('Searching').getAttribute('aria-checked')).toBe('true');
    expect(radioText()).toEqual(['Searching 250', 'Not found 1', 'All 251']);
    expect(heading()).toBe('Wanted 250');
    expect(el.querySelectorAll('tbody tr').length).toBe(1);
    expect(text(el)).not.toContain('Alien (1979)');
    expect(text(el)).toContain('Showing the first 1 of 250.');

    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ notFound: false });

    radio('All').click();
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith(undefined);
    expect(heading()).toBe('Wanted 251');
    expect(el.querySelectorAll('tbody tr').length).toBe(2);

    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({});

    radio('Not found').click();
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith(true);
    expect(heading()).toBe('Wanted 1');
    expect(text(el)).toContain('Not found');
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();

    // Retrying moves the movie back to "Searching" and the counts follow.
    button(el, 'Retry').click();
    await settle(fixture);
    expect(api.retrySubtitles).toHaveBeenLastCalledWith(30);
    expect(radioText()).toEqual(['Searching 251', 'Not found 0', 'All 251']);
    expect(heading()).toBe('Wanted 0');
    expect(text(el)).toContain('No movies are marked as not found.');
    expect([...el.querySelectorAll('button')].some(b => b.textContent?.trim() === 'Retry all')).toBe(false);
  });

  it('refreshes the list and counts when subtitles change', async () => {
    let counts = { searching: 2, notFound: 0 };
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [],
        wantedSubtitles: async () => wanted,
        wantedSubtitleCounts: async () => counts,
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h2')!.textContent).toContain('2');

    counts = { searching: 1, notFound: 1 };
    const events = TestBed.inject(Events);
    events.subtitlesUpdated.next({ movieId: 3 });
    events.subtitlesUpdated.next({ movieId: 3 });
    await new Promise(r => setTimeout(r, 600));
    await settle(fixture);
    expect(api.wantedSubtitleCounts).toHaveBeenCalledTimes(2); // debounced: one refresh for both events
    expect(el.querySelector('h2')!.textContent!.replace(/\s+/g, ' ')).toContain('Wanted 1');
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
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl'], notFound: false });

    // Selecting a second provider passes both.
    button(el, 'SubDL').click();
    await settle(fixture);
    checkboxes()[0].click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl', 'opensubtitles'], notFound: false });

    // Clearing the selection falls back to searching all configured providers.
    button(el, '2 providers').click();
    await settle(fixture);
    button(el, 'All providers').click();
    await settle(fixture);
    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ notFound: false });
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
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ providers: ['subdl'], notFound: false });
  });
});
