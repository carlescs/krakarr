import { signal } from '@angular/core';

/**
 * Waits between lookups while Jellyfin doesn't have a title yet. Krakarr asks Jellyfin to rescan right after an import,
 * but the scan takes a while, so the first lookup for a fresh download usually finds nothing. About four minutes in all.
 */
export const JELLYFIN_RETRY_DELAYS = [10_000, 20_000, 40_000, 80_000, 120_000];

/**
 * The link behind a "Play in Jellyfin" button. {@link watch} looks the title up when its file appears or changes and
 * retries a few times while Jellyfin hasn't picked the file up yet. Failures just leave the button hidden.
 */
export class JellyfinLink {
  readonly url = signal<string | null>(null);
  private key: string | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;

  /**
   * Looks the title up for `key` (which identifies the downloaded file; null when there is none).
   * Calls with the same key do nothing, so this can run on every reload of the page.
   */
  watch(key: string | null, fetch: () => Promise<{ url: string } | null>) {
    if (key === this.key) return;
    clearTimeout(this.timer);
    this.key = key;
    this.url.set(null);
    if (key != null) this.lookup(key, fetch, 0);
  }

  /** Forgets the title (navigation to another one, or the page closing) and stops retrying. */
  reset() {
    clearTimeout(this.timer);
    this.key = null;
    this.url.set(null);
  }

  private async lookup(key: string, fetch: () => Promise<{ url: string } | null>, attempt: number) {
    let url: string | null = null;
    try {
      url = (await fetch())?.url ?? null;
    } catch {
      // Not configured or unreachable: no button, but a later attempt may still work.
    }
    if (key !== this.key) return;
    if (url) {
      this.url.set(url);
      return;
    }
    if (attempt < JELLYFIN_RETRY_DELAYS.length) {
      this.timer = setTimeout(() => this.lookup(key, fetch, attempt + 1), JELLYFIN_RETRY_DELAYS[attempt]);
    }
  }
}
