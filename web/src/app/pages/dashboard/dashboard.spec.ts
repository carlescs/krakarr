import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { DashboardStats, TorrentStatus } from '../../core/models';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { DashboardPage, TOP_DOWNLOADS } from './dashboard';

function stats(overrides: Partial<DashboardStats> = {}): DashboardStats {
  return {
    library: {
      movies: { total: 120, monitored: 110, downloaded: 100, missing: 10 },
      series: { total: 15, monitored: 14, continuing: 6, ended: 9 },
      episodes: { total: 900, downloaded: 800, missing: 25, airingThisWeek: 3 },
      files: 700, sizeOnDisk: 5 * 1024 ** 4, subtitles: 300,
      resolutions: [{ resolution: 2160, files: 50, size: 2 * 1024 ** 4 }, { resolution: 1080, files: 650, size: 3 * 1024 ** 4 }],
    },
    users: { total: 4, admins: 1, managers: 1, users: 2, topRequesters: [{ username: 'dora', requests: 7 }] },
    requests: {
      total: 9, pending: 2, approved: 6, declined: 1, last30Days: 4,
      recent: [{ id: 1, mediaType: 'Series', title: 'Severance', year: 2022, status: 'Pending', requestedBy: 'dora', requestedAt: '2026-01-01T00:00:00Z' }],
    },
    activity: { grabbed: 12, imported: 10, failed: 1, subtitles: 5, importIssues: 2 },
    recentlyImported: [{ mediaType: 'Movie', id: 3, title: 'Alien', year: 1979, date: '2026-01-01T00:00:00Z' }],
    recentlyAdded: [{ mediaType: 'Series', id: 8, title: 'Andor', year: 2022, date: '2026-01-02T00:00:00Z' }],
    ...overrides,
  };
}

function torrent(id: number, overrides: Partial<TorrentStatus> = {}): TorrentStatus {
  return {
    id, infoHash: `h${id}`, name: `Torrent ${id}`, state: 'Downloading', progress: 50, size: 1000, downloaded: 500, uploaded: 0,
    downloadRate: 100, uploadRate: 10, seeds: 1, leechers: 1, ratio: 0, category: 'movies', savePath: '/d', added: '2026-01-01T00:00:00Z',
    imported: false, client: 'embedded', ...overrides,
  };
}

describe('DashboardPage', () => {
  it('shows library, request, user and activity figures', async () => {
    const api = setupTest({
      api: mockApi({
        dashboard: async () => stats(), calendar: async () => [],
        rootFolders: async () => [{ id: 1, path: '/media/movies', mediaType: 'Movie', accessible: true, freeSpace: 1024 ** 4 }],
      }),
    });
    const fixture = TestBed.createComponent(DashboardPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(api.dashboard).toHaveBeenCalled();
    expect(text(el, '[data-stat="movies"]')).toContain('120');
    expect(text(el, '[data-stat="movies"]')).toContain('10 missing');
    expect(text(el, '[data-stat="episodes"]')).toContain('of 900 · 25 missing');
    expect(text(el, '[data-stat="disk"]')).toContain('5.0 TB');
    expect(text(el, '[data-stat="requests"]')).toContain('2');
    expect(text(el, '[data-stat="users"]')).toContain('2 staff');
    expect(text(el, '[data-list="resolutions"]')).toContain('2160p');
    expect(text(el, '[data-list="root-folders"]')).toContain('1.0 TB free');
    expect(text(el, '[data-list="requesters"]')).toContain('dora');
    expect(text(el, '[data-list="recent-requests"]')).toContain('Severance');
    expect(text(el, '[data-section="activity"]')).toContain('3 episodes air in the next 7 days');
    expect(text(el, '[data-section="downloads"]')).toContain('2 imports need attention');
    expect(el.querySelector('[data-list="recently-imported"] a[href="/movies/3"]')).not.toBeNull();
    expect(text(el, '[data-section="downloads"]')).toContain('Nothing in the download client.');
  });

  it('summarises live downloads', async () => {
    setupTest({ api: mockApi({ dashboard: async () => stats(), rootFolders: async () => [], calendar: async () => [] }) });
    const fixture = TestBed.createComponent(DashboardPage);
    const events = TestBed.inject(Events);
    const active = Array.from({ length: TOP_DOWNLOADS + 2 }, (_, i) => torrent(i + 1, { downloadRate: i * 10, movieId: i + 1 }));
    events.downloads.set([...active, torrent(50, { state: 'Seeding', progress: 100, downloaded: 1000 }), torrent(51, { state: 'Error', downloadRate: 0 })]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el, '[data-stat="active"]')).toBe(String(TOP_DOWNLOADS + 2));
    expect(el.querySelectorAll('[data-list="active-downloads"] li').length).toBe(TOP_DOWNLOADS);
    // Fastest first.
    expect(text(el, '[data-list="active-downloads"] li')).toContain(`Torrent ${TOP_DOWNLOADS + 2}`);
    expect(text(el, '[data-section="downloads"]')).toContain('and 2 more');
    expect(text(el, '[data-section="downloads"]')).toContain('50.0% of');
    expect(text(el, '[data-section="downloads"]')).toContain('Seeding 1');
    expect(text(el, '[data-section="downloads"]')).toContain('1 download with errors');
  });

  it('shows empty states', async () => {
    setupTest({
      api: mockApi({
        dashboard: async () => stats({
          requests: { total: 0, pending: 0, approved: 0, declined: 0, last30Days: 0, recent: [] },
          recentlyImported: [],
          recentlyAdded: [],
          users: { total: 1, admins: 1, managers: 0, users: 0, topRequesters: [] },
        }),
        rootFolders: async () => [],
      }),
    });
    const fixture = TestBed.createComponent(DashboardPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '[data-section="requests"]')).toContain('No requests yet.');
    expect(text(el, '[data-section="recent"]')).toContain('Nothing imported yet.');
    expect(el.querySelector('[data-list="root-folders"]')).toBeNull();
  });
});
