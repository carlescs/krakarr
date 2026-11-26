import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { describe, expect, it, vi } from 'vitest';
import { WantedSubtitle, WantedSubtitlePage, WantedSubtitleQuery } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { Events } from '../../core/events';
import { SubtitlesPage } from './subtitles';

const wanted: WantedSubtitle[] = [
  { mediaType: 'Movie', movieId: 3, movieFileId: 30, label: 'Alien (1979)', missingLanguages: ['es'] },
  { mediaType: 'Series', seriesId: 4, episodeFileId: 40, label: 'Dark S01E01', missingLanguages: ['en', 'es'] },
];
/** A server page of `all` filtered like the API does, with the totals per filter. */
function serve(all: WantedSubtitle[], q: WantedSubtitleQuery & { page: number; pageSize: number }): WantedSubtitlePage {
  const filtered = q.notFound == null ? all : all.filter(w => !!w.notFound === q.notFound);
  const notFound = all.filter(w => w.notFound).length;
  return {
    items: filtered.slice((q.page - 1) * q.pageSize, q.page * q.pageSize), page: q.page, pageSize: q.pageSize,
    total: filtered.length, libraryTotal: all.length, searching: all.length - notFound, notFound,
  };
}
const list = async (q: WantedSubtitleQuery & { page: number; pageSize: number }) => serve(wanted, q);
const settings = { enabled: true, openSubtitles: { apiKey: 'key' }, languages: ['es'], searchIntervalHours: 6 };

describe('SubtitlesPage', () => {
  it('lists files missing subtitles and searches them', async () => {
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        wantedSubtitles: list,
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
        wantedSubtitles: async q => serve(marked ? [notFound, wanted[1]] : [wanted[0], wanted[1]], q),
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

    // Default: only files still searched automatically; every page reports the totals per filter.
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith({ notFound: false, page: 1, pageSize: 60 });
    expect(radio('Searching').getAttribute('aria-checked')).toBe('true');
    expect(radioText()).toEqual(['Searching 1', 'Not found 1', 'All 2']);
    expect(heading()).toBe('Wanted 1');
    expect(text(el)).not.toContain('Alien (1979)');
    expect(text(el)).toContain('Dark S01E01');

    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({ notFound: false });

    radio('All').click();
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith({ page: 1, pageSize: 60 });
    expect(heading()).toBe('Wanted 2');
    expect(text(el)).toContain('Alien (1979)');

    button(el, 'Search all missing').click();
    await settle(fixture);
    expect(api.searchSubtitles).toHaveBeenLastCalledWith({});

    radio('Not found').click();
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith({ notFound: true, page: 1, pageSize: 60 });
    expect(heading()).toBe('Wanted 1');
    expect(text(el)).toContain('Not found');
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();

    // Retrying moves the movie back to "Searching" and the counts follow.
    button(el, 'Retry').click();
    await settle(fixture);
    expect(api.retrySubtitles).toHaveBeenLastCalledWith({ movieFileId: 30, episodeFileId: undefined });
    expect(radioText()).toEqual(['Searching 2', 'Not found 0', 'All 2']);
    expect(heading()).toBe('Wanted 0');
    expect(text(el)).toContain('No files are marked as not found.');
    expect([...el.querySelectorAll('button')].some(b => b.textContent?.trim() === 'Retry all')).toBe(false);
  });

  it('loads further pages as the list scrolls and sorts on the server', async () => {
    const many: WantedSubtitle[] = Array.from({ length: 130 }, (_, i) => (
      { mediaType: 'Series', seriesId: 4, episodeFileId: 100 + i, label: `Dark S01E${i + 1}`, missingLanguages: ['es'] }));
    const api = setupTest({
      api: mockApi({ settings: async () => settings, subtitleLanguages: async () => [], wantedSubtitles: async q => serve(many, q) }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h2')!.textContent!.replace(/\s+/g, ' ')).toContain('Wanted 130');
    expect(api.wantedSubtitles).toHaveBeenCalledTimes(1);

    const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport)).componentInstance as CdkVirtualScrollViewport;
    viewport.setRenderedRange({ start: 120, end: 130 });
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenLastCalledWith({ notFound: false, page: 3, pageSize: 60 });
    expect(text(el)).toContain('Dark S01E125');

    button(el, 'Missing').click();
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenCalledWith({ notFound: false, sort: 'missing', desc: true, page: 1, pageSize: 60 });
  });

  it('marks every file still searched as not found after confirming', async () => {
    let marked = false;
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [],
        wantedSubtitles: async q => serve(marked ? wanted.map(w => ({ ...w, notFound: '2026-11-20T10:00:00Z' })) : wanted, q),
        markAllSubtitlesNotFound: async () => { marked = true; return { count: 2 }; },
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    button(el, 'Mark all not found').click();
    await settle(fixture);
    expect(confirmSpy).toHaveBeenCalled();
    expect(api.markAllSubtitlesNotFound).not.toHaveBeenCalled();

    confirmSpy.mockReturnValue(true);
    button(el, 'Mark all not found').click();
    await settle(fixture);
    expect(api.markAllSubtitlesNotFound).toHaveBeenCalledTimes(1);
    expect(text(el)).toContain('No files are waiting for an automatic search.');
    expect([...el.querySelectorAll('button')].some(b => b.textContent?.trim() === 'Mark all not found')).toBe(false);
    confirmSpy.mockRestore();
  });

  it('refreshes the list and counts when subtitles change', async () => {
    let current = wanted;
    const api = setupTest({
      api: mockApi({ settings: async () => settings, subtitleLanguages: async () => [], wantedSubtitles: async q => serve(current, q) }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('h2')!.textContent).toContain('2');

    current = [wanted[0], { ...wanted[1], notFound: '2026-11-20T10:00:00Z' }];
    const calls = vi.mocked(api.wantedSubtitles).mock.calls.length;
    const events = TestBed.inject(Events);
    events.subtitlesUpdated.next({ movieId: 3 });
    events.subtitlesUpdated.next({ movieId: 3 });
    await new Promise(r => setTimeout(r, 600));
    await settle(fixture);
    expect(api.wantedSubtitles).toHaveBeenCalledTimes(calls + 1); // collapsed: one refresh for both events
    expect(el.querySelector('h2')!.textContent!.replace(/\s+/g, ' ')).toContain('Wanted 1');
  });

  it('warns admins when no provider is configured', async () => {
    setupTest({
      api: mockApi({ settings: async () => ({ ...settings, openSubtitles: { apiKey: '' } }), subtitleLanguages: async () => [], wantedSubtitles: list }),
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
        wantedSubtitles: list,
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
        wantedSubtitles: list,
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
        wantedSubtitles: list,
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

  it('switches to the tab of downloaded subtitles a video already has embedded', async () => {
    const api = setupTest({
      api: mockApi({
        settings: async () => settings,
        subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }],
        wantedSubtitles: list,
        redundantSubtitles: async () => ({ items: [], offset: 0, limit: 100, total: 0 }),
      }),
    });
    const fixture = TestBed.createComponent(SubtitlesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('kr-redundant-subtitles')).toBeNull();

    button(el, 'Already embedded').click();
    await settle(fixture);
    expect(el.querySelector('kr-redundant-subtitles')).not.toBeNull();
    expect(api.redundantSubtitles).toHaveBeenCalled();
    expect(el.textContent).not.toContain('Search all missing');
    expect(el.querySelector('[role="tab"][aria-selected="true"]')?.textContent?.trim()).toBe('Already embedded');
  });
});
