import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ListCache } from './library';

describe('ListCache', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function cache() {
    let n = 0;
    const fetch = vi.fn(async () => [++n]);
    const onError = vi.fn();
    return { list: new ListCache<number>(fetch, onError, 1000), fetch, onError };
  }

  it('keeps the last list across visits and refreshes on attach', async () => {
    const { list, fetch } = cache();
    expect(list.items()).toBeNull();
    const detach = list.attach();
    await vi.runAllTimersAsync();
    expect(list.items()).toEqual([1]);
    detach();

    list.attach();
    expect(list.items()).toEqual([1]); // shown straight away
    await vi.runAllTimersAsync();
    expect(list.items()).toEqual([2]);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('collapses a burst of updates into one reload', async () => {
    const { list, fetch } = cache();
    list.attach();
    await vi.runAllTimersAsync();
    for (let i = 0; i < 50; i++) list.invalidate();
    await vi.advanceTimersByTimeAsync(999);
    expect(fetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('ignores updates while no page shows the list', async () => {
    const { list, fetch } = cache();
    list.attach()();
    await vi.runAllTimersAsync();
    list.invalidate();
    await vi.runAllTimersAsync();
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it('reloads again when an update arrives during a load', async () => {
    let resolve!: (v: number[]) => void;
    const fetch = vi.fn(() => new Promise<number[]>(r => (resolve = r)));
    const list = new ListCache<number>(fetch, () => {}, 0);
    list.attach();
    list.invalidate();
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1);
    resolve([1]);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);
  });

  it('shows an empty list when the first load fails', async () => {
    const list = new ListCache<number>(() => Promise.reject(new Error('down')), () => {}, 0);
    list.attach();
    await vi.runAllTimersAsync();
    expect(list.items()).toEqual([]);
  });
});
