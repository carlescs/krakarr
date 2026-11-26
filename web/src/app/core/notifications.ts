import { Injectable, computed, inject, signal } from '@angular/core';
import { interval, merge, Subscription } from 'rxjs';
import { Api, errorMessage } from './api';
import { Events } from './events';
import { AppNotification, NotificationAction, NotificationProgressUpdate, NotificationTray } from './models';
import { Toasts } from './toast';

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
  private readonly toasts = inject(Toasts);
  private subscription?: Subscription;

  /** Every notification, whichever tray lists it. */
  readonly items = signal<AppNotification[]>([]);
  /** Notification ids whose button is being run, to disable it meanwhile. */
  readonly busy = signal<ReadonlySet<string>>(new Set());
  private readonly trays = {
    bell: this.trayState('bell'),
    subtitleSync: this.trayState('subtitleSync'),
  } satisfies Record<NotificationTray, unknown>;

  /** One tray's notifications, the sum its badge shows and whether any of them is a problem. */
  tray(tray: NotificationTray) {
    return this.trays[tray];
  }

  private trayState(tray: NotificationTray) {
    const items = computed(() => this.items().filter(n => (n.tray ?? 'bell') === tray));
    return {
      items,
      total: computed(() => items().reduce((sum, n) => sum + n.count, 0)),
      hasProblems: computed(() => items().some(n => n.severity === 'Warning' || n.severity === 'Error')),
      /** Waiting or running work (progress still going), as opposed to outcomes. */
      busy: computed(() => items().some(n => n.progress != null && n.severity === 'Info')),
    };
  }

  /** Count of one notification by id (e.g. "requests.pending"), for badges elsewhere. */
  count(id: string): number {
    return this.items().find(n => n.id === id)?.count ?? 0;
  }

  start() {
    if (this.subscription) return;
    this.subscription = merge(this.events.notificationsUpdated, this.events.requestsUpdated, interval(REFRESH_INTERVAL_MS)).subscribe(() => this.refresh());
    this.subscription.add(this.events.notificationProgress.subscribe(p => this.applyProgress(p)));
    this.refresh();
  }

  stop() {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.items.set([]);
  }

  /** Updates a notification with work in progress in place; ones not shown (dismissed, or not loaded yet) are left alone. */
  private applyProgress(update: NotificationProgressUpdate) {
    this.items.update(items => items.map(n => n.id !== update.id ? n : {
      ...n,
      message: update.message ?? n.message,
      progress: { percent: update.percent ?? null, remainingSeconds: update.remainingSeconds ?? null },
    }));
  }

  async refresh() {
    try {
      this.items.set(await this.api.notifications());
    } catch {
      // Keep the last known state; the bell is not worth an error toast.
    }
  }

  /** Runs a notification's button; the server publishes the change, so the bell refreshes by itself. */
  async act(n: AppNotification, action: NotificationAction) {
    this.busy.update(ids => new Set(ids).add(n.id));
    try {
      await this.api.notificationAction(action.path);
    } catch (e) {
      this.toasts.error(errorMessage(e));
      await this.refresh();
    } finally {
      this.busy.update(ids => { const next = new Set(ids); next.delete(n.id); return next; });
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

  /** Dismisses everything in one tray. */
  async dismissAll(tray: NotificationTray = 'bell') {
    this.items.update(items => items.filter(n => (n.tray ?? 'bell') !== tray));
    try {
      await this.api.dismissAllNotifications(tray);
    } catch {
      await this.refresh();
    }
  }
}
