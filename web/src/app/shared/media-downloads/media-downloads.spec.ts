import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { TorrentStatus, UserRole } from '../../core/models';
import { button, settle, setupTest, text } from '../../../testing/test-setup';
import { MediaDownloads } from './media-downloads';

const torrent = (t: Partial<TorrentStatus>): TorrentStatus => ({
  id: 1, infoHash: 'h1', name: 'Dark.S01E01', state: 'Downloading', progress: 42, size: 1024, downloaded: 0, uploaded: 0, downloadRate: 0, uploadRate: 0,
  seeds: 3, leechers: 1, ratio: 0, category: 'tv', savePath: '/downloads', added: '2026-01-01T00:00:00Z', imported: false, client: 'embedded', ...t,
});

async function render(downloads: TorrentStatus[], role: UserRole = 'Admin', labels = new Map<string, string>()) {
  setupTest({ role });
  const fixture = TestBed.createComponent(MediaDownloads);
  fixture.componentRef.setInput('downloads', downloads);
  fixture.componentRef.setInput('labels', labels);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

describe('MediaDownloads', () => {
  afterEach(() => localStorage.clear());

  it('renders nothing without torrents', async () => {
    const { el } = await render([]);
    expect(el.querySelector('section')).toBeNull();
  });

  it('links each torrent to the downloads page, with its episodes', async () => {
    const { el } = await render(
      [torrent({}), torrent({ id: 2, infoHash: 'h2', name: 'Dark.S01E02', state: 'Seeding', progress: 100, imported: true, ratio: 1.5 })],
      'Admin', new Map([['h1', 'S01E01']]));
    expect(text(el, 'h2')).toBe('Download');
    expect(el.querySelectorAll('[data-active-download]').length).toBe(1);
    expect(text(el, '[data-active-download]')).toContain('S01E01');
    expect(text(el, '[data-seeding-download]')).toContain('Ratio 1.50');
    const links = [...el.querySelectorAll<HTMLAnchorElement>('a[data-download-link]')].map(a => a.getAttribute('href'));
    expect(links).toEqual(['/downloads?hash=h1', '/downloads?hash=h2']);
    expect(el.querySelector('a[href="/downloads"]')).not.toBeNull();
  });

  it('shows plain users the torrents without links', async () => {
    const { el } = await render([torrent({})], 'User');
    expect(text(el)).toContain('Dark.S01E01');
    expect(el.querySelector('a')).toBeNull();
  });

  it('shows a few imported torrents, then all of them on request', async () => {
    const seeding = Array.from({ length: 5 }, (_, i) =>
      torrent({ id: i + 1, infoHash: 'h' + i, name: 'Pack.' + i, state: 'Seeding', progress: 100, imported: true }));
    const { fixture, el } = await render(seeding);
    expect(text(el, 'h2')).toBe('Seeding');
    expect(el.querySelectorAll('[data-seeding-download]').length).toBe(3);
    button(el, 'Show all 5').click();
    await settle(fixture);
    expect(el.querySelectorAll('[data-seeding-download]').length).toBe(5);
  });

  it('hides the torrents on request and remembers it', async () => {
    const { fixture, el } = await render([torrent({}), torrent({ id: 2, infoHash: 'h2' })]);
    button(el, 'Hide').click();
    await settle(fixture);
    expect(el.querySelector('[data-active-download]')).toBeNull();
    expect(text(el, '[data-hidden-count]')).toBe('(2)');
    expect(localStorage.getItem('krakarr.mediaDownloads.hidden')).toBe('true');

    // A freshly opened page starts hidden.
    const again = TestBed.createComponent(MediaDownloads);
    again.componentRef.setInput('downloads', [torrent({})]);
    await settle(again);
    const el2 = again.nativeElement as HTMLElement;
    expect(el2.querySelector('[data-active-download]')).toBeNull();
    button(el2, 'Show').click();
    await settle(again);
    expect(el2.querySelectorAll('[data-active-download]').length).toBe(1);
    expect(localStorage.getItem('krakarr.mediaDownloads.hidden')).toBe('false');
  });
});
