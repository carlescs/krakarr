import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { TorrentStatus } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { DownloadsPage } from './downloads';

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
