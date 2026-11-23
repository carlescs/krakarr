import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PagedListCache, PagedResponse } from './paged-list';

interface Item { id: number; label: string; }
interface Query { filter?: string; q?: string; }

function pageOf(items: Item[], page: number, pageSize: number, total = items.length, libraryTotal = total): PagedResponse<Item> {
  const start = (page - 1) * pageSize;
  return { items: items.slice(start, start + pageSize), page, pageSize, total, libraryTotal };
}

describe('PagedListCache', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function makeCache(pageSize = 2, delayMs = 1000, maxCachedPages = 20) {
    let calls = 0;
    const fetch = vi.fn(async (_q: Query, page: number, size: number): Promise<PagedResponse<Item>> => {
      calls++;
      const total = 6;
      const items: Item[] = Array.from({ length: total }, (_, i) => ({ id: i + 1, label: `v${calls}-${i + 1}` }));
      return pageOf(items, page, size, total, total);
    });
    const onError = vi.fn();
    const list = new PagedListCache<Item, Query>({ filter: 'all' }, fetch, onError, pageSize, delayMs, maxCachedPages);
    return { list, fetch, onError };
  }

  it('keeps cached pages across visits and refreshes in the background on attach', async () => {
    const { list, fetch } = makeCache();
    expect(list.loading()).toBe(true);
    const detach = list.attach();
    await vi.runAllTimersAsync();
    expect(list.loading()).toBe(false);
    expect(list.at(0)?.label).toBe('v1-1');
    expect(list.total()).toBe(6);
    detach();

    list.attach();
    expect(list.at(0)?.label).toBe('v1-1'); // shown straight away
    await vi.runAllTimersAsync();
    expect(list.at(0)?.label).toBe('v2-1');
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('collapses a burst of updates into one reload', async () => {
    const { list, fetch } = makeCache();
    list.attach();
    await vi.runAllTimersAsync();
    for (let i = 0; i < 50; i++) list.invalidate();
    await vi.advanceTimersByTimeAsync(999);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('ignores updates while no page shows the list', async () => {
    const { list, fetch } = makeCache();
    list.attach()();
    await vi.runAllTimersAsync();
    list.invalidate();
    await vi.runAllTimersAsync();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('reloads again when an update arrives during a reload', async () => {
    let resolveReload!: (v: PagedResponse<Item>) => void;
    let calls = 0;
    const fetch = vi.fn((_q: Query, page: number, size: number) => {
      calls++;
      if (calls === 2) return new Promise<PagedResponse<Item>>(r => (resolveReload = r));
      return Promise.resolve(pageOf([{ id: 1, label: `c${calls}` }], page, size, 1, 1));
    });
    const list = new PagedListCache<Item, Query>({}, fetch, () => {}, 2, 0);
    list.attach();
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toBe(1);

    void list.reload();
    list.invalidate();
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toBe(2);

    resolveReload(pageOf([{ id: 1, label: 'c2' }], 1, 2, 1, 1));
    await vi.advanceTimersByTimeAsync(0);
    expect(calls).toBe(3);
  });

  it('fetches missing pages via ensureRange and evicts LRU pages beyond maxCachedPages', async () => {
    const { list, fetch } = makeCache(2, 1000, 2);
    list.attach();
    await vi.runAllTimersAsync();
    expect(list.at(0)?.id).toBe(1);
    expect(list.at(2)).toBeUndefined();

    // Load page 2 (indices 2..3)
    list.ensureRange(2, 3);
    await vi.runAllTimersAsync();
    expect(list.at(2)?.id).toBe(3);
    expect(list.idsBetween(1, 4)).toEqual([1, 2, 3, 4]);

    // Load page 3 (indices 4..5) -> maxCachedPages = 2 so page 1 (LRU) is evicted
    list.ensureRange(4, 5);
    await vi.runAllTimersAsync();
    expect(list.at(4)?.id).toBe(5);
    expect(list.at(0)).toBeUndefined();
    expect(list.idsBetween(1, 5)).toBeNull();
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('discards stale responses when the query changes mid-flight', async () => {
    const pending: Array<{ q: Query; resolve: (v: PagedResponse<Item>) => void }> = [];
    const fetch = vi.fn((q: Query) => new Promise<PagedResponse<Item>>(resolve => pending.push({ q, resolve })));
    const list = new PagedListCache<Item, Query>({ filter: 'all' }, fetch, () => {}, 2, 0);
    list.attach();
    list.setQuery({ filter: 'missing' });

    // Resolve the second query first, then the stale first query.
    pending[1].resolve(pageOf([{ id: 9, label: 'missing' }], 1, 2, 1, 5));
    await vi.runAllTimersAsync();
    expect(list.at(0)?.id).toBe(9);

    pending[0].resolve(pageOf([{ id: 1, label: 'all' }], 1, 2, 1, 5));
    await vi.runAllTimersAsync();
    expect(list.at(0)?.id).toBe(9);
  });

  it('stops loading and reports error when the first load fails', async () => {
    const onError = vi.fn();
    const list = new PagedListCache<Item, Query>({}, () => Promise.reject(new Error('down')), onError, 2, 0);
    list.attach();
    await vi.runAllTimersAsync();
    expect(list.loading()).toBe(false);
    expect(list.total()).toBe(0);
    expect(onError).toHaveBeenCalledTimes(1);
  });
});
