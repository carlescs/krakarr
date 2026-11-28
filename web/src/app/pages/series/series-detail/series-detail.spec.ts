import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { Toasts } from '../../../core/toast';
import { SeriesDetailPage, episodeCodes } from './series-detail';

describe('SeriesDetailPage', () => {
  it('reloads and resets per-title state when the id input changes', async () => {
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: `Series ${id}`, monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [{ seasonNumber: id }] })),
      episodes: vi.fn(async (id: number) => [{ id: id * 100, seriesId: id, seasonNumber: id, episodeNumber: 1 }]),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      wantedSubtitleLanguages: vi.fn(async () => ({ enabled: false, useEmbeddedSubtitles: true, languages: [] })),
      subtitleLanguages: vi.fn(async () => []),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(SeriesDetailPage, { set: { template: '', imports: [] } });

    type Page = {
      series: () => { id: number } | null;
      episodes: () => { seriesId: number }[];
      expanded: () => Set<number>;
      confirmDelete: { set(v: boolean): void; (): boolean };
    };
    const fixture = TestBed.createComponent(SeriesDetailPage);
    const page = fixture.componentInstance as unknown as Page;
    fixture.componentRef.setInput('id', 1);
    await fixture.whenStable();
    expect(page.series()?.id).toBe(1);
    expect([...page.expanded()]).toEqual([1]);

    page.confirmDelete.set(true);
    fixture.componentRef.setInput('id', 2);
    await fixture.whenStable();
    expect(api.series).toHaveBeenLastCalledWith(2);
    expect(page.series()?.id).toBe(2);
    expect(page.episodes().map(e => e.seriesId)).toEqual([2]);
    expect([...page.expanded()]).toEqual([2]);
    expect(page.confirmDelete()).toBe(false);
  });

  it('saves an edited title, trimmed', async () => {
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: 'Same Name', metadataTitle: 'Same Name', monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [] })),
      episodes: vi.fn(async () => []),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      wantedSubtitleLanguages: vi.fn(async () => ({ enabled: false, useEmbeddedSubtitles: true, languages: [] })),
      subtitleLanguages: vi.fn(async () => []),
      updateSeries: vi.fn(async () => undefined),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(SeriesDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(SeriesDetailPage);
    const page = fixture.componentInstance as unknown as { editTitle: string; saveEdit(): Promise<void>; toggleSeries(): Promise<void> };
    fixture.componentRef.setInput('id', 3);
    await fixture.whenStable();
    expect(page.editTitle).toBe('Same Name');

    page.editTitle = '  Same Name (US) ';
    await page.saveEdit();
    expect(api.updateSeries).toHaveBeenLastCalledWith(3, true, 1, true, null, 'Same Name (US)');

    // Toggling monitoring leaves the title alone.
    await page.toggleSeries();
    expect(api.updateSeries).toHaveBeenLastCalledWith(3, false, 1, true, null);
  });

  it('marks downloaded episodes missing a wanted subtitle language, per season', async () => {
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: 'Show', monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [{ seasonNumber: 1 }] })),
      episodes: vi.fn(async () => [
        { id: 1, seriesId: 3, seasonNumber: 1, episodeNumber: 1, episodeFile: { id: 10 } },
        { id: 2, seriesId: 3, seasonNumber: 1, episodeNumber: 2, episodeFile: { id: 11 } },
        { id: 3, seriesId: 3, seasonNumber: 1, episodeNumber: 3 },
      ]),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => [
        { id: 1, episodeFileId: 10, language: 'en', forced: false, provider: 'opensubtitles' },
        { id: 2, episodeFileId: 10, language: 'es', forced: false, provider: 'opensubtitles' },
        { id: 3, episodeFileId: 11, language: 'en', forced: false, provider: 'opensubtitles' },
      ]),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      wantedSubtitleLanguages: vi.fn(async () => ({ enabled: true, useEmbeddedSubtitles: true, languages: ['en', 'es'] })),
      subtitleLanguages: vi.fn(async () => []),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(SeriesDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(SeriesDetailPage);
    const page = fixture.componentInstance as unknown as { missingSubtitles: () => Map<number, string[]>; seasonSubtitleGaps: () => Map<number, number> };
    fixture.componentRef.setInput('id', 3);
    await fixture.whenStable();
    await new Promise(resolve => setTimeout(resolve));
    expect([...page.missingSubtitles()]).toEqual([[11, ['es']]]);
    expect(page.seasonSubtitleGaps().get(1)).toBe(1);
  });

  it('reads which episodes were watched, per server', async () => {
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: 'Show', monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [{ seasonNumber: 1 }], episodeFileCount: 2 })),
      episodes: vi.fn(async () => []),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      wantedSubtitleLanguages: vi.fn(async () => ({ enabled: false, useEmbeddedSubtitles: true, languages: [] })),
      subtitleLanguages: vi.fn(async () => []),
      seriesJellyfinLink: vi.fn(async () => null),
      seriesPlayStatus: vi.fn(async () => [
        { server: 'Jellyfin', watched: 1, total: 2, episodes: [{ episodeId: 1, played: true }, { episodeId: 2, played: false, progress: 0.5 }] },
        { server: 'Plex', watched: 1, total: 2, episodes: [{ episodeId: 1, played: true }] },
      ]),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(SeriesDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(SeriesDetailPage);
    const page = fixture.componentInstance as unknown as { episodePlay: () => Map<number, { server: string; played: boolean; progress?: number }[]> };
    fixture.componentRef.setInput('id', 3);
    await fixture.whenStable();
    await new Promise(resolve => setTimeout(resolve));
    expect(api.seriesPlayStatus).toHaveBeenCalledWith(3);
    expect(page.episodePlay().get(1)?.map(s => s.server)).toEqual(['Jellyfin', 'Plex']);
    expect(page.episodePlay().get(2)).toEqual([expect.objectContaining({ server: 'Jellyfin', played: false, progress: 0.5 })]);
  });

  it('deletes all files of a season once each, then reloads', async () => {
    const file = (id: number, relativePath: string) => ({ id, relativePath, size: 1, quality: 'HDTV-720p', dateAdded: '2024-01-01T00:00:00Z' });
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: 'Show', monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [{ seasonNumber: 1 }, { seasonNumber: 2 }] })),
      episodes: vi.fn(async () => [
        { id: 1, seasonNumber: 1, episodeNumber: 1, episodeFile: file(10, 'Season 01/S01E01-E02.mkv') },
        { id: 2, seasonNumber: 1, episodeNumber: 2, episodeFile: file(10, 'Season 01/S01E01-E02.mkv') },
        { id: 3, seasonNumber: 1, episodeNumber: 3 },
        { id: 4, seasonNumber: 2, episodeNumber: 1, episodeFile: file(20, 'Season 02/S02E01.mkv') },
      ]),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      wantedSubtitleLanguages: vi.fn(async () => ({ enabled: false, useEmbeddedSubtitles: true, languages: [] })),
      subtitleLanguages: vi.fn(async () => []),
      deleteEpisodeFiles: vi.fn(async () => ({ affected: 1, errors: [] })),
    };
    const toasts = { error: vi.fn(), show: vi.fn(), success: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: toasts },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(SeriesDetailPage, { set: { template: '', imports: [] } });

    type Page = {
      confirmDeleteSeasonFiles(season: number): void;
      fileDeletion: () => { fileIds: number[]; paths: string[] } | null;
      deleteEpisodeFiles(unmonitor: boolean): Promise<void>;
      filesDeleted(): Promise<void>;
    };
    const fixture = TestBed.createComponent(SeriesDetailPage);
    const page = fixture.componentInstance as unknown as Page;
    fixture.componentRef.setInput('id', 1);
    await fixture.whenStable();

    page.confirmDeleteSeasonFiles(1);
    expect(page.fileDeletion()).toMatchObject({ fileIds: [10], paths: ['Season 01/S01E01-E02.mkv'] });

    await page.deleteEpisodeFiles(true);
    expect(api.deleteEpisodeFiles).toHaveBeenCalledWith([10], true);
    expect(toasts.success).toHaveBeenCalledWith('File deleted');

    api.series.mockClear();
    await page.filesDeleted();
    expect(page.fileDeletion()).toBeNull();
    expect(api.series).toHaveBeenCalledWith(1);
  });

  it('collapses consecutive episodes into ranges, per season', () => {
    const e = (seasonNumber: number, episodeNumber: number) => ({ seasonNumber, episodeNumber });
    expect(episodeCodes([e(1, 2), e(1, 1), e(1, 3), e(1, 5), e(2, 1)])).toBe('S01E01–E03 E05, S02E01');
    expect(episodeCodes([e(3, 7)])).toBe('S03E07');
  });
});
