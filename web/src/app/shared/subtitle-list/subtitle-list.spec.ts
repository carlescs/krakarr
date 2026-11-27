import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { SubtitleFile } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitleList } from './subtitle-list';

const external = { id: 5, language: 'es', languageName: 'Spanish', hearingImpaired: true, forced: false, provider: 'opensubtitles', score: 92,
  release: 'Alien.1979.1080p.BluRay', fileName: 'Alien.es.hi.srt', dateAdded: new Date().toISOString() } as SubtitleFile;
const embedded = { id: 0, language: 'en', languageName: 'English', hearingImpaired: false, forced: false, provider: 'embedded',
  fileName: 'Alien.mkv', dateAdded: new Date().toISOString() } as SubtitleFile;

function setup(wanted: { enabled: boolean; useEmbeddedSubtitles: boolean; languages: string[] }) {
  return setupTest({
    api: mockApi({
      wantedSubtitleLanguages: async () => wanted,
      subtitleLanguages: async () => [{ code: 'en', name: 'English' }, { code: 'es', name: 'Spanish' }, { code: 'fr', name: 'French' }],
      deleteSubtitle: async () => undefined,
      shiftSubtitle: async () => ({}),
      syncSubtitle: async () => ({}),
    }),
  });
}

describe('SubtitleList', () => {
  it('shows each subtitle in detail and which wanted languages are missing', async () => {
    setup({ enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es', 'fr'] });
    const fixture = TestBed.createComponent(SubtitleList);
    fixture.componentRef.setInput('files', [embedded, external]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    const rows = [...el.querySelectorAll('[data-subtitle]')].map(r => text(r as HTMLElement));
    expect(rows[0]).toContain('Spanish (HI)');
    expect(rows[0]).toContain('opensubtitles');
    expect(rows[0]).toContain('92');
    expect(rows[0]).toContain('Alien.1979.1080p.BluRay');
    expect(rows[1]).toContain('English');
    expect(rows[1]).toContain('Embedded');
    expect([...el.querySelectorAll('[data-missing]')].map(m => text(m as HTMLElement))).toEqual(['French · missing']);
    expect(el.querySelector('[aria-label="Delete subtitle"]')).toBeNull();
  });

  it('asks to find a missing language when it is searchable', async () => {
    setup({ enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es', 'fr'] });
    const fixture = TestBed.createComponent(SubtitleList);
    fixture.componentRef.setInput('files', [embedded, external]);
    const find = vi.fn();
    fixture.componentInstance.find.subscribe(find);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('button[data-missing]')).toBeNull();

    fixture.componentRef.setInput('searchable', true);
    await settle(fixture);
    (el.querySelector('button[data-missing]') as HTMLButtonElement).click();
    expect(find).toHaveBeenCalledWith('fr');
  });

  it('ignores embedded tracks when they are not used', async () => {
    setup({ enabled: true, useEmbeddedSubtitles: false, languages: ['en'] });
    const fixture = TestBed.createComponent(SubtitleList);
    fixture.componentRef.setInput('files', [embedded]);
    await settle(fixture);
    expect(text(fixture.nativeElement, '[data-missing]')).toBe('English · missing');
  });

  it('hides coverage when subtitles are disabled and shows an empty state', async () => {
    setup({ enabled: false, useEmbeddedSubtitles: true, languages: ['en'] });
    const fixture = TestBed.createComponent(SubtitleList);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-coverage]')).toBeNull();
    expect(text(el)).toContain('No subtitles yet.');
  });

  it('deletes external subtitles only', async () => {
    const api = setup({ enabled: true, useEmbeddedSubtitles: true, languages: [] });
    const fixture = TestBed.createComponent(SubtitleList);
    fixture.componentRef.setInput('files', [embedded, external]);
    fixture.componentRef.setInput('deletable', true);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('[aria-label="Delete subtitle"]').length).toBe(1);

    button(el, 'Delete subtitle').click();
    await settle(fixture);
    expect(api.deleteSubtitle).toHaveBeenCalledWith(5);
    expect(changed).toHaveBeenCalled();
  });

  it('shows sync results and shifts or syncs external subtitles', async () => {
    const api = setup({ enabled: true, useEmbeddedSubtitles: true, languages: [] });
    const fixture = TestBed.createComponent(SubtitleList);
    const synced = { ...external, manual: true, syncedAt: new Date().toISOString(), syncNote: 'Synced with alass against the audio' };
    const failed = { ...external, id: 6, language: 'fr', languageName: 'French', syncNote: 'Sync failed: no reference' };
    fixture.componentRef.setInput('files', [embedded, synced, failed]);
    fixture.componentRef.setInput('deletable', true);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-synced]')?.getAttribute('title')).toBe('Synced with alass against the audio');
    expect(el.querySelector('[data-sync-failed]')?.getAttribute('title')).toBe('Sync failed: no reference');
    expect(text(el)).toContain('Manual');
    expect(el.querySelectorAll('[aria-label="Subtitle timing"]').length).toBe(2);

    button(el, 'Subtitle timing').click();
    await settle(fixture);
    const manual = el.querySelector('[data-manual-timing]') as HTMLDetailsElement;
    expect(manual.open).toBe(false);
    manual.open = true;
    button(el, '−1 s').click();
    button(el, '+0.1 s').click();
    await settle(fixture);
    button(el, 'Shift').click();
    await settle(fixture);
    expect(api.shiftSubtitle).toHaveBeenCalledWith(6, -900);
    expect(changed).toHaveBeenCalled();

    button(el, 'Sync with video').click();
    await settle(fixture);
    expect(api.syncSubtitle).toHaveBeenCalledWith(6);
    expect(el.querySelector('[data-timing]')).toBeNull();
  });
});
