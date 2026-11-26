import { Injectable, computed, inject, signal } from '@angular/core';
import { interval, merge, Subscription } from 'rxjs';
import { Api } from './api';
import { Events } from './events';
import { AppNotification } from './models';

/** Some notifications depend on the date (e.g. today's releases), so also refetch now and then without an event. */
const REFRESH_INTERVAL_MS = 30 * 60 * 1000;

/**
 * The bell's contents. The server computes notifications from every registered source; this keeps them fresh by
 * refetching whenever an event says a source may have changed. To feed a new kind of notification, add an
 * INotificationSource on the server and publish `notificationsUpdated` (or an existing event listed below).
 */
@Injectable({ providedIn: 'root' })
export class Notifications {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private subscription?: Subscription;

  readonly items = signal<AppNotification[]>([]);
  /** What the bell's badge shows. */
  readonly total = computed(() => this.items().reduce((sum, n) => sum + n.count, 0));
  readonly hasProblems = computed(() => this.items().some(n => n.severity !== 'Info'));

  /** Count of one notification by id (e.g. "requests.pending"), for badges elsewhere. */
  count(id: string): number {
    return this.items().find(n => n.id === id)?.count ?? 0;
  }

  start() {
    if (this.subscription) return;
    this.subscription = merge(this.events.notificationsUpdated, this.events.requestsUpdated, interval(REFRESH_INTERVAL_MS)).subscribe(() => this.refresh());
    this.refresh();
  }

  stop() {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.items.set([]);
  }

  async refresh() {
    try {
      this.items.set(await this.api.notifications());
    } catch {
      // Keep the last known state; the bell is not worth an error toast.
    }
  }

  async dismiss(id: string) {
    this.items.update(items => items.filter(n => n.id !== id));
    try {
      await this.api.dismissNotification(id);
    } catch {
      await this.refresh();
    }
  }

  async dismissAll() {
    this.items.set([]);
    try {
      await this.api.dismissAllNotifications();
    } catch {
      await this.refresh();
    }
  }
}
