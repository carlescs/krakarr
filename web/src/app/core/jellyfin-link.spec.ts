import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { JELLYFIN_RETRY_DELAYS, JellyfinLink } from './jellyfin-link';

const flush = () => vi.advanceTimersByTimeAsync(0);

describe('JellyfinLink', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('retries until Jellyfin has scanned a fresh download', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(null).mockRejectedValueOnce(new Error('down')).mockResolvedValue({ url: 'https://jf/item' });
    const link = new JellyfinLink();
    link.watch('1:10', fetch);
    await flush();
    expect(link.url()).toBeNull();
    await vi.advanceTimersByTimeAsync(JELLYFIN_RETRY_DELAYS[0]);
    expect(link.url()).toBeNull();
    await vi.advanceTimersByTimeAsync(JELLYFIN_RETRY_DELAYS[1]);
    expect(link.url()).toBe('https://jf/item');
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('gives up after the last retry', async () => {
    const fetch = vi.fn().mockResolvedValue(null);
    const link = new JellyfinLink();
    link.watch('1:10', fetch);
    await vi.advanceTimersByTimeAsync(JELLYFIN_RETRY_DELAYS.reduce((a, b) => a + b, 0) * 2);
    expect(fetch).toHaveBeenCalledTimes(JELLYFIN_RETRY_DELAYS.length + 1);
  });

  it('ignores reloads of the same file and looks up again when it changes', async () => {
    const fetch = vi.fn().mockResolvedValue({ url: 'https://jf/item' });
    const link = new JellyfinLink();
    link.watch('1:10', fetch);
    link.watch('1:10', fetch);
    await flush();
    expect(fetch).toHaveBeenCalledTimes(1);
    link.watch('1:11', fetch);
    await flush();
    expect(fetch).toHaveBeenCalledTimes(2);
    link.watch(null, fetch);
    expect(link.url()).toBeNull();
  });

  it('stops retrying after a reset', async () => {
    const fetch = vi.fn().mockResolvedValue(null);
    const link = new JellyfinLink();
    link.watch('1:10', fetch);
    await flush();
    link.reset();
    await vi.advanceTimersByTimeAsync(JELLYFIN_RETRY_DELAYS[0] * 10);
    expect(fetch).toHaveBeenCalledTimes(1);
  });
});
