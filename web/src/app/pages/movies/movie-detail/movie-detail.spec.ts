import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { Toasts } from '../../../core/toast';
import { MovieDetailPage } from './movie-detail';

describe('MovieDetailPage', () => {
  it('reloads and resets per-title state when the id input changes', async () => {
    const api = {
      movie: vi.fn(async (id: number) => ({ id, title: `Movie ${id}`, monitored: true, qualityProfileId: 1, minimumAvailability: 'Released' })),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), movieUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(MovieDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(MovieDetailPage);
    const page = fixture.componentInstance as unknown as { movie: () => { id: number } | null; editing: { set(v: boolean): void; (): boolean } };
    fixture.componentRef.setInput('id', 1);
    await fixture.whenStable();
    expect(api.movie).toHaveBeenLastCalledWith(1);
    expect(page.movie()?.id).toBe(1);

    page.editing.set(true);
    fixture.componentRef.setInput('id', 2);
    await fixture.whenStable();
    expect(api.movie).toHaveBeenLastCalledWith(2);
    expect(api.subtitleFiles).toHaveBeenLastCalledWith({ movieId: 2 });
    expect(page.movie()?.id).toBe(2);
    expect(page.editing()).toBe(false);
  });

  it('triggers a movie refresh via the API', async () => {
    const api = {
      movie: vi.fn(async (id: number) => ({ id, title: `Movie ${id}`, monitored: true, qualityProfileId: 1, minimumAvailability: 'Released' })),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      refreshMovie: vi.fn(async () => ({ id: 'cmd-1' })),
    };
    const toasts = { error: vi.fn(), show: vi.fn(), success: vi.fn() };
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), movieUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: toasts },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(MovieDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(MovieDetailPage);
    fixture.componentRef.setInput('id', 7);
    await fixture.whenStable();

    await fixture.componentInstance.refresh();
    expect(api.refreshMovie).toHaveBeenCalledWith(7);
    expect(toasts.show).toHaveBeenCalled();
  });

  it('looks the movie up on Jellyfin and reads its play status once its file is imported', async () => {
    let movieFile: { id: number } | null = null;
    const api = {
      movie: vi.fn(async (id: number) => ({ id, title: `Movie ${id}`, monitored: true, qualityProfileId: 1, minimumAvailability: 'Released', hasFile: !!movieFile, movieFile })),
      qualityProfiles: vi.fn(async () => []),
      history: vi.fn(async () => ({ items: [] })),
      subtitleFiles: vi.fn(async () => []),
      languages: vi.fn(async () => ({ all: [], preferred: [] })),
      movieJellyfinLink: vi.fn(async () => ({ url: 'https://jf/web/#/details?id=m' })),
      moviePlayStatus: vi.fn(async () => [{ server: 'Jellyfin', played: true, playCount: 1 }]),
    };
    const movieUpdated = new Subject<{ id: number }>();
    TestBed.configureTestingModule({
      providers: [
        { provide: Api, useValue: api },
        { provide: Auth, useValue: { isStaff: () => true } },
        { provide: Events, useValue: { downloads: signal([]), movieUpdated, historyAdded: new Subject(), subtitlesUpdated: new Subject() } },
        { provide: Toasts, useValue: { error: vi.fn(), show: vi.fn(), success: vi.fn() } },
        { provide: Router, useValue: { navigateByUrl: vi.fn() } },
      ],
    });
    TestBed.overrideComponent(MovieDetailPage, { set: { template: '', imports: [] } });

    const fixture = TestBed.createComponent(MovieDetailPage);
    const page = fixture.componentInstance as unknown as { jellyfinUrl: () => string | null; playStatus: () => { played: boolean }[] };
    fixture.componentRef.setInput('id', 1);
    await fixture.whenStable();
    expect(api.movieJellyfinLink).not.toHaveBeenCalled();
    expect(api.moviePlayStatus).not.toHaveBeenCalled();

    movieFile = { id: 10 };
    movieUpdated.next({ id: 1 });
    await fixture.whenStable();
    await new Promise(r => setTimeout(r));
    expect(api.movieJellyfinLink).toHaveBeenCalledWith(1);
    expect(page.jellyfinUrl()).toBe('https://jf/web/#/details?id=m');
    expect(page.playStatus()).toEqual([expect.objectContaining({ played: true })]);

    // Further updates of the same file don't look it up again.
    movieUpdated.next({ id: 1 });
    await fixture.whenStable();
    expect(api.movieJellyfinLink).toHaveBeenCalledTimes(1);
    expect(api.moviePlayStatus).toHaveBeenCalledTimes(1);
  });
});
