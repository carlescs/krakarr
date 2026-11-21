import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { Api } from './api';
import { Auth } from './auth';
import { MediaType, Subscription } from './models';

/**
 * The library titles the signed-in user follows, for the "My titles" list filters. Loaded on demand and kept in sync
 * when the user subscribes or unsubscribes from this tab.
 */
@Injectable({ providedIn: 'root' })
export class Subscriptions {
  private readonly api = inject(Api);
  private readonly list = signal<Subscription[] | null>(null);
  private loading?: Promise<void>;

  constructor() {
    // Another user may log in next: never show them the previous session's list.
    const auth = inject(Auth);
    effect(() => { if (auth.status()?.authenticated === false) this.list.set(null); });
  }

  readonly movieIds = computed(() => new Set((this.list() ?? []).filter(s => s.mediaType === 'Movie').map(s => s.libraryId)));
  readonly seriesIds = computed(() => new Set((this.list() ?? []).filter(s => s.mediaType === 'Series').map(s => s.libraryId)));

  /** Loads the list (again); failures leave the previous list in place. */
  refresh(): Promise<void> {
    this.loading ??= this.api.subscriptions()
      .then(list => this.list.set(list))
      .catch(() => { /* keep what we had */ })
      .finally(() => this.loading = undefined);
    return this.loading;
  }

  async set(mediaType: MediaType, id: number, subscribed: boolean): Promise<void> {
    await (subscribed ? this.api.subscribe(mediaType, id) : this.api.unsubscribe(mediaType, id));
    if (this.list() !== null) void this.refresh();
  }
}
