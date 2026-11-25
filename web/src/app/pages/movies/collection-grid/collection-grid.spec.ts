import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Events } from '../../../core/events';
import { MovieCollectionSummary } from '../../../core/models';
import { mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { CollectionGrid } from './collection-grid';

const lotr: MovieCollectionSummary = { id: 1, tmdbId: 119, name: 'The Lord of the Rings Collection', posterUrl: 'https://img/lotr.jpg', count: 3, downloaded: 3 };
const alien: MovieCollectionSummary = { id: 2, tmdbId: 8091, name: 'Alien Collection', count: 4, downloaded: 1 };

async function render(collections: MovieCollectionSummary[], inputs: Record<string, unknown> = {}) {
  const api = setupTest({ api: mockApi({ movieCollections: async () => collections }) });
  const fixture = TestBed.createComponent(CollectionGrid);
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('CollectionGrid', () => {
  afterEach(() => vi.useRealTimers());

  it('shows a card per collection with its downloaded share', async () => {
    const { api, el } = await render([alien, lotr], { q: 'ring', filter: 'downloaded' });
    expect(api.movieCollections).toHaveBeenCalledWith({ q: 'ring', filter: 'downloaded' });
    const cards = [...el.querySelectorAll('a')];
    expect(cards.map(a => a.getAttribute('href'))).toEqual(['/movies/collections/2', '/movies/collections/1']);
    expect(text(cards[0])).toContain('1 / 4 downloaded');
    expect((cards[0].querySelector('[style*="width"]') as HTMLElement).style.width).toBe('25%');
    expect(cards[1].querySelector('img')?.getAttribute('src')).toBe('https://img/lotr.jpg');
  });

  it('explains an empty result', async () => {
    const { el } = await render([]);
    expect(text(el)).toContain('None of your movies belongs to a collection yet.');
  });

  it('reloads after library changes', async () => {
    const { api, fixture } = await render([alien]);
    vi.useFakeTimers();
    const events = TestBed.inject(Events);
    events.movieUpdated.next({ id: 1 });
    events.movieUpdated.next({ id: 2 });
    vi.advanceTimersByTime(1000);
    vi.useRealTimers();
    await settle(fixture);
    expect(api.movieCollections).toHaveBeenCalledTimes(2);
  });
});
