import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { TorrentDetails } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { TorrentDetailsDialog } from './torrent-details';

const details = {
  client: 'Built-in',
  status: { hash: 'abc', name: 'Alien.1979.1080p', state: 'Downloading', progress: 50, size: 1024, downloadRate: 0, uploadRate: 0, movieId: 3, added: '2026-01-01T00:00:00Z', savePath: '/downloads' },
  files: [{ index: 0, path: 'Alien.mkv', size: 1024, progress: 50, priority: 'Normal' }],
  peers: [],
  trackers: [{ url: 'udp://tracker.test', tier: 0, status: 'Working' }],
  tags: [], webSeeds: [],
} as unknown as TorrentDetails;

describe('TorrentDetailsDialog', () => {
  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }));
  afterEach(() => vi.useRealTimers());

  it('shows the torrent and switches tabs', async () => {
    const api = setupTest({ api: mockApi({ downloadDetails: async () => details }) });
    const fixture = TestBed.createComponent(TorrentDetailsDialog);
    fixture.componentRef.setInput('hash', 'abc');
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.downloadDetails).toHaveBeenCalledWith('abc');
    expect(text(el, 'h2')).toBe('Alien.1979.1080p');
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();
    expect(text(el)).toContain('/downloads');

    button(el, 'Files').click();
    await settle(fixture);
    expect(text(el)).toContain('Alien.mkv');

    button(el, 'Peers').click();
    await settle(fixture);
    expect(text(el)).toContain('Not connected to any peers.');

    button(el, 'Trackers').click();
    await settle(fixture);
    expect(text(el)).toContain('udp://tracker.test');

    // Refreshes every two seconds until closed.
    vi.advanceTimersByTime(2000);
    expect(api.downloadDetails).toHaveBeenCalledTimes(2);
    fixture.destroy();
    vi.advanceTimersByTime(4000);
    expect(api.downloadDetails).toHaveBeenCalledTimes(2);
  });

  it('shows errors', async () => {
    setupTest({ api: mockApi({ downloadDetails: async () => { throw new Error('Torrent not found'); } }) });
    const fixture = TestBed.createComponent(TorrentDetailsDialog);
    fixture.componentRef.setInput('hash', 'abc');
    fixture.componentRef.setInput('name', 'Alien');
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Torrent not found');
    fixture.destroy();
  });
});
