import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { TorrentStatus } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { DownloadsPage, sortTorrents } from './downloads';

const torrent = (t: Partial<TorrentStatus>): TorrentStatus => ({
  id: 1, infoHash: 'h1', name: 'Alien.1979.1080p', state: 'Downloading', progress: 42, size: 1024, downloaded: 0, uploaded: 0, downloadRate: 0, uploadRate: 0,
  seeds: 3, leechers: 1, ratio: 0, category: 'movies', savePath: '/downloads', added: '2026-01-01T00:00:00Z', imported: false, ...t,
});
const torrents = [torrent({ movieId: 3 }), torrent({ id: 2, infoHash: 'h2', name: 'Dark.S01', state: 'Seeding', progress: 100, seriesId: 4 })];

async function render(extra: Record<string, (...args: never[]) => unknown> = {}) {
  const api = setupTest({
    api: mockApi({ downloadClientStatus: async () => ({ kind: 'BuiltIn' }), downloads: async () => torrents, ...extra }),
  });
  const fixture = TestBed.createComponent(DownloadsPage);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('DownloadsPage', () => {
  afterEach(() => localStorage.clear());

  it('shows downloading torrents and switches to seeding ones', async () => {
    const { fixture, el } = await render();
    expect(text(el)).toContain('Built-in client');
    expect(text(el)).toContain('Alien.1979.1080p');
    expect(text(el)).not.toContain('Dark.S01');
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();

    button(el, 'Seeding (1)').click();
    await settle(fixture);
    expect(text(el)).toContain('Dark.S01');
    expect(text(el)).not.toContain('Alien.1979.1080p');
    expect(localStorage.getItem('krakarr.downloads.showSeeding')).toBe('true');
  });

  it('pauses and removes torrents', async () => {
    const { api, fixture, el } = await render({ pauseDownload: async () => undefined, removeDownload: async () => undefined });
    button(el, '❚❚').click();
    expect(api.pauseDownload).toHaveBeenCalledWith('h1');

    button(el, '✕').click();
    await settle(fixture);
    (el.querySelector('[role="dialog"] input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    button(el, 'Remove').click();
    await settle(fixture);
    expect(api.removeDownload).toHaveBeenCalledWith('h1', true);
    expect(TestBed.inject(Events).downloads().map(d => d.infoHash)).toEqual(['h2']);
  });

  it('pauses, resumes and removes a selection in bulk', async () => {
    const extra = torrent({ id: 3, infoHash: 'h3', name: 'Heat.1995', state: 'Paused' });
    const { api, fixture, el } = await render({
      downloads: async () => [...torrents, extra],
      bulkDownloads: async () => ({ affected: 2, errors: [] }),
    });
    expect(text(el)).toContain('Select all (2)');
    (el.querySelector('input[aria-label="Select Alien.1979.1080p"]') as HTMLInputElement).click();
    (el.querySelector('input[aria-label="Select Heat.1995"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(el)).toContain('2 selected');

    button(el.querySelector('[role="toolbar"]') as HTMLElement, 'Pause').click();
    await settle(fixture);
    expect(api.bulkDownloads).toHaveBeenCalledWith(['h1', 'h3'], 'Pause', false);
    button(el.querySelector('[role="toolbar"]') as HTMLElement, 'Resume').click();
    await settle(fixture);
    expect(api.bulkDownloads).toHaveBeenCalledWith(['h1', 'h3'], 'Resume', false);

    button(el, 'Remove…').click();
    await settle(fixture);
    const dialog = el.querySelector('[role="dialog"]') as HTMLElement;
    expect(text(dialog)).toContain('Remove 2 torrents');
    (dialog.querySelector('input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    button(dialog, 'Remove').click();
    await settle(fixture);
    expect(api.bulkDownloads).toHaveBeenCalledWith(['h1', 'h3'], 'Remove', true);
    expect(el.querySelector('[role="dialog"]')).toBeNull();
    expect(text(el)).toContain('Select all');
  });

  it('selects all torrents in the current view only', async () => {
    const { fixture, el } = await render();
    (el.querySelector('input[aria-label="Select all torrents"]') as HTMLInputElement).click();
    await settle(fixture);
    expect(text(el)).toContain('1 selected');
    button(el, 'Seeding (1)').click();
    await settle(fixture);
    expect(text(el)).toContain('Select all (1)');
  });

  it('sorts by ETA, soonest first, with unknown ETAs last', async () => {
    const list = [
      torrent({ infoHash: 'a', name: 'Slow', etaSeconds: 9000 }),
      torrent({ infoHash: 'b', name: 'Stalled', etaSeconds: undefined }),
      torrent({ infoHash: 'c', name: 'Fast', etaSeconds: 60 }),
      torrent({ infoHash: 'd', name: 'Paused', state: 'Paused', etaSeconds: 30 }),
    ];
    const { fixture, el } = await render({ downloads: async () => list });
    const names = () => [...el.querySelectorAll('input[aria-label^="Select "]:not([aria-label="Select all torrents"])')]
      .map(i => i.getAttribute('aria-label')!.replace('Select ', ''));
    expect(names()).toEqual(['Slow', 'Stalled', 'Fast', 'Paused']); // client order (newest first)

    const select = el.querySelector('#downloads-sort') as HTMLSelectElement;
    select.value = 'eta';
    select.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(names()).toEqual(['Fast', 'Slow', 'Stalled', 'Paused']);
    expect(JSON.parse(localStorage.getItem('krakarr.downloads.sort')!)).toEqual({ key: 'eta', desc: false });

    button(el, 'Sort ascending').click();
    await settle(fixture);
    expect(names()).toEqual(['Slow', 'Fast', 'Stalled', 'Paused']);
  });

  it('sorts names naturally and numbers by size', () => {
    const list = [torrent({ name: 'Show S10', size: 5 }), torrent({ name: 'show S2', size: 50 }), torrent({ name: 'Alpha', size: 1 })];
    expect(sortTorrents(list, { key: 'name', desc: false }).map(d => d.name)).toEqual(['Alpha', 'show S2', 'Show S10']);
    expect(sortTorrents(list, { key: 'size', desc: true }).map(d => d.size)).toEqual([50, 5, 1]);
  });

  it('adds a magnet link', async () => {
    const { api, fixture, el } = await render({ addMagnet: async () => undefined, movies: async () => [], seriesList: async () => [] });
    button(el, '+ Add torrent').click();
    await settle(fixture);
    type(el, 'textarea', 'magnet:?xt=urn:btih:abc&dn=Some.Movie');
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Add').click();
    await settle(fixture);
    expect(api.addMagnet).toHaveBeenCalledWith('magnet:?xt=urn:btih:abc&dn=Some.Movie', 'manual', {});
    expect(el.querySelector('[role="dialog"]')).toBeNull();
  });

  it('reports an unreachable download client', async () => {
    const { el } = await render({ downloadClientStatus: async () => ({ kind: 'QBittorrent', tag: 'krakarr', error: 'connection refused' }) });
    expect(text(el)).toContain('qBittorrent · tag krakarr');
    expect(text(el)).toContain('Cannot reach the download client: connection refused');
  });
});
