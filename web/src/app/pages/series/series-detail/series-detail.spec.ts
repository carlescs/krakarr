import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { Toasts } from '../../../core/toast';
import { SeriesDetailPage } from './series-detail';

describe('SeriesDetailPage', () => {
  it('reloads and resets per-title state when the id input changes', async () => {
    const api = {
      series: vi.fn(async (id: number) => ({ id, title: `Series ${id}`, monitored: true, seasonFolder: true, qualityProfileId: 1, seasons: [{ seasonNumber: id }] })),
      episodes: vi.fn(async (id: number) => [{ id: id * 100, seriesId: id, seasonNumber: id, episodeNumber: 1 }]),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), seriesUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject(), searchProgress: new Subject() } },
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
});
