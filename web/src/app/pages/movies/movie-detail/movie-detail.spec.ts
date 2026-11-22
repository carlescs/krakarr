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
        { provide: Events, useValue: { downloads: signal([]), movieUpdated: new Subject(), historyAdded: new Subject(), subtitlesUpdated: new Subject(), searchProgress: new Subject() } },
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
});
