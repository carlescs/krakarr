import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RedundantSubtitle } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { RedundantSubtitles } from './redundant-subtitles';

const rows: RedundantSubtitle[] = [
  {
    id: 7, mediaType: 'Series', seriesId: 4, episodeFileId: 40, label: 'Dark S01E01', language: 'de', languageName: 'German',
    hearingImpaired: false, forced: false, fileName: 'Dark - S01E01.de.srt', provider: 'opensubtitles', dateAdded: '2026-11-20T10:00:00Z',
    embedded: [{ language: 'de', languageName: 'German', hearingImpaired: false, forced: false }, { language: 'en', languageName: 'English', hearingImpaired: true, forced: false }],
  },
  {
    id: 8, mediaType: 'Movie', movieId: 3, movieFileId: 30, label: 'Alien (1979)', language: 'en', languageName: 'English',
    hearingImpaired: false, forced: false, fileName: 'Alien (1979).en.srt', provider: 'subdl', dateAdded: '2026-11-21T10:00:00Z',
    embedded: [{ language: 'en', languageName: 'English', hearingImpaired: false, forced: false }],
  },
];

describe('RedundantSubtitles', () => {
  afterEach(() => vi.restoreAllMocks());

  it('lists subtitles that duplicate an embedded track and deletes one after confirming', async () => {
    const api = setupTest({
      api: mockApi({
        redundantSubtitles: async () => ({ items: rows, offset: 0, limit: 100, total: 2 }),
        deleteSubtitle: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(RedundantSubtitles);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.redundantSubtitles).toHaveBeenCalledWith({ offset: 0, limit: 100 });
    expect(text(el, 'h2')).toBe('Already embedded 2');
    expect(el.querySelector('a[href="/series/4"]')?.textContent).toBe('Dark S01E01');
    expect(el.querySelector('a[href="/movies/3"]')?.textContent).toBe('Alien (1979)');
    expect(text(el)).toContain('en SDH');

    // Cancelling the prompt keeps the file.
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    button(el, 'Delete Dark - S01E01.de.srt').click();
    await settle(fixture);
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Dark - S01E01.de.srt'));
    expect(api.deleteSubtitle).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    button(el, 'Delete Dark - S01E01.de.srt').click();
    await settle(fixture);
    expect(api.deleteSubtitle).toHaveBeenCalledWith(7);
    expect(el.querySelector('a[href="/series/4"]')).toBeNull();
    expect(text(el, 'h2')).toBe('Already embedded 1');
  });

  it('selects rows and deletes them in one request after confirming', async () => {
    const api = setupTest({
      api: mockApi({
        redundantSubtitles: async () => ({ items: rows, offset: 0, limit: 100, total: 2 }),
        deleteSubtitles: async () => ({ affected: 2, errors: [] }),
      }),
    });
    const fixture = TestBed.createComponent(RedundantSubtitles);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="toolbar"]')).toBeNull();

    const pick = (label: string) => el.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!;
    pick('Select Dark - S01E01.de.srt').click();
    await settle(fixture);
    expect(text(el, '[role="toolbar"]')).toContain('1 selected');
    expect(pick('Select all').indeterminate).toBe(true);

    pick('Select all').click();
    await settle(fixture);
    expect(text(el, '[role="toolbar"]')).toContain('2 selected');

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    button(el, 'Delete selected').click();
    await settle(fixture);
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Delete 2 downloaded subtitles'));
    expect(api.deleteSubtitles).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    button(el, 'Delete selected').click();
    await settle(fixture);
    expect(api.deleteSubtitles).toHaveBeenCalledWith([7, 8]);
    expect(text(el, 'h2')).toBe('Already embedded 0');
    expect(text(el)).toContain('No downloaded subtitle duplicates an embedded track.');
  });

  it('reloads after a bulk delete that partly failed', async () => {
    const api = setupTest({
      api: mockApi({
        redundantSubtitles: async () => ({ items: rows, offset: 0, limit: 100, total: 2 }),
        deleteSubtitles: async () => ({ affected: 1, errors: ['Alien (1979).en.srt: access denied'] }),
      }),
    });
    const fixture = TestBed.createComponent(RedundantSubtitles);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    el.querySelector<HTMLInputElement>('input[aria-label="Select all"]')!.click();
    await settle(fixture);

    api.redundantSubtitles.mockImplementation(async () => ({ items: [rows[1]], offset: 0, limit: 100, total: 1 }));
    button(el, 'Delete selected').click();
    await settle(fixture);
    expect(api.redundantSubtitles).toHaveBeenCalledTimes(2);
    expect(el.querySelector('a[href="/series/4"]')).toBeNull();
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
    expect(el.querySelector('[role="toolbar"]')).toBeNull();
  });

  it('says when nothing duplicates an embedded track and loads more on request', async () => {
    const api = setupTest({ api: mockApi({ redundantSubtitles: async () => ({ items: [], offset: 0, limit: 100, total: 0 }) }) });
    const fixture = TestBed.createComponent(RedundantSubtitles);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No downloaded subtitle duplicates an embedded track.');

    api.redundantSubtitles.mockImplementation(async ({ offset }) =>
      offset === 0 ? { items: [rows[0]], offset, limit: 100, total: 2 } : { items: [rows[1]], offset, limit: 100, total: 2 });
    await fixture.componentInstance.load();
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Show more').click();
    await settle(fixture);
    expect(api.redundantSubtitles).toHaveBeenLastCalledWith({ offset: 1, limit: 100 });
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
    expect(el.textContent).not.toContain('Show more');
  });

  it('keeps rows appended by "Show more" when an older reload finishes after it', async () => {
    const api = setupTest({ api: mockApi({ redundantSubtitles: async () => ({ items: [rows[0]], offset: 0, limit: 100, total: 2 }) }) });
    const fixture = TestBed.createComponent(RedundantSubtitles);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    let finishReload!: () => void;
    api.redundantSubtitles.mockImplementation(({ offset }) => offset === 0
      ? new Promise(resolve => { finishReload = () => resolve({ items: [rows[0]], offset: 0, limit: 100, total: 2 }); })
      : Promise.resolve({ items: [rows[1]], offset, limit: 100, total: 2 }));
    const reload = fixture.componentInstance.load();
    button(el, 'Show more').click();
    await settle(fixture);
    finishReload();
    await reload;
    await settle(fixture);
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
    expect(el.querySelector('a[href="/series/4"]')).not.toBeNull();
  });
});
