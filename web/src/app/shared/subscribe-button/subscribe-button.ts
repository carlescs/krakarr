import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { errorMessage, Api } from '../../core/api';
import { MediaType } from '../../core/models';
import { Subscriptions } from '../../core/subscriptions';
import { Toasts } from '../../core/toast';

/**
 * Follow / unfollow a library title. Followed titles show under "My titles" in the calendar and the library filters.
 * Staff also see who follows it.
 */
@Component({
  selector: 'kr-subscribe-button',
  templateUrl: './subscribe-button.html',
  styleUrl: './subscribe-button.scss',
  host: { class: 'contents' },
})
export class SubscribeButton {
  private readonly api = inject(Api);
  private readonly subscriptions = inject(Subscriptions);
  private readonly toasts = inject(Toasts);

  readonly mediaType = input.required<MediaType>();
  readonly libraryId = input.required<number>();
  /** Accent style while not followed; off where another action is the page's main one. */
  readonly primary = input(true);

  /** Null while loading. */
  protected readonly subscribed = signal<boolean | null>(null);
  protected readonly subscribers = signal<string[] | null>(null);
  protected readonly busy = signal(false);

  constructor() {
    effect(() => {
      const type = this.mediaType(), id = this.libraryId();
      untracked(() => this.load(type, id));
    });
  }

  private async load(type: MediaType, id: number) {
    this.subscribed.set(null);
    this.subscribers.set(null);
    try {
      const state = await this.api.subscriptionState(type, id);
      if (type !== this.mediaType() || id !== this.libraryId()) return;
      this.subscribed.set(state.subscribed);
      this.subscribers.set(state.subscribers ?? null);
    } catch {
      this.subscribed.set(false);
    }
  }

  protected async toggle() {
    const next = !this.subscribed();
    const type = this.mediaType(), id = this.libraryId();
    this.busy.set(true);
    try {
      await this.subscriptions.set(type, id, next);
      this.subscribed.set(next);
      // Staff see the follower list: reload it so their own name appears or goes.
      if (this.subscribers() !== null) await this.load(type, id);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
