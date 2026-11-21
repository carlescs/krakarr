import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { describe, expect, it, vi } from 'vitest';
import { Api } from './api';
import { Auth } from './auth';
import { AuthStatus, Subscription } from './models';
import { Subscriptions } from './subscriptions';

const sub = (mediaType: 'Movie' | 'Series', libraryId: number): Subscription =>
  ({ mediaType, libraryId, title: 'T', year: 2020, subscribedAt: '2026-11-21T00:00:00Z' });

describe('Subscriptions', () => {
  function setup(initial: Subscription[]) {
    let list = initial;
    const api = {
      subscriptions: vi.fn(async () => list),
      subscribe: vi.fn(async (t: 'Movie' | 'Series', id: number) => { list = [...list, sub(t, id)]; }),
      unsubscribe: vi.fn(async (t: 'Movie' | 'Series', id: number) => { list = list.filter(s => s.mediaType !== t || s.libraryId !== id); }),
    };
    const status = signal<AuthStatus | null>({ setupRequired: false, authenticated: true });
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }, { provide: Auth, useValue: { status } }] });
    return { service: TestBed.inject(Subscriptions), api, status };
  }

  it('splits followed titles by kind and follows changes', async () => {
    const { service, api } = setup([sub('Movie', 1), sub('Series', 1), sub('Movie', 7)]);
    expect(service.movieIds().size).toBe(0);
    await service.refresh();
    expect([...service.movieIds()]).toEqual([1, 7]);
    expect([...service.seriesIds()]).toEqual([1]);

    await service.set('Series', 3, true);
    await service.refresh();
    expect(api.subscribe).toHaveBeenCalledWith('Series', 3);
    expect(service.seriesIds().has(3)).toBe(true);

    await service.set('Movie', 7, false);
    await service.refresh();
    expect(service.movieIds().has(7)).toBe(false);
  });

  it('forgets the list on logout', async () => {
    const { service, status } = setup([sub('Movie', 1)]);
    await service.refresh();
    status.set({ setupRequired: false, authenticated: false });
    TestBed.tick();
    expect(service.movieIds().size).toBe(0);
  });
});
