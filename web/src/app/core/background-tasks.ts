import { Injectable, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { Api, errorMessage } from './api';
import { Events } from './events';
import { BackgroundTask, BackgroundTaskProgress } from './models';
import { Toasts } from './toast';

const ACTIVE: ReadonlySet<BackgroundTask['state']> = new Set(['Waiting', 'Running', 'Cancelling']);

/** Waiting, running or stopping, as opposed to finished. */
export function isActive(task: BackgroundTask) {
  return ACTIVE.has(task.state);
}

/**
 * Long-running work (e.g. subtitle syncs) for the tasks menu in the top bar: the tasks the signed-in user started,
 * plus automatic ones for staff (the server filters). The list is refetched when the server says it changed
 * (`backgroundTasksUpdated`); progress in between arrives as `backgroundTaskProgress` and is applied in place.
 */
@Injectable({ providedIn: 'root' })
export class BackgroundTasks {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly toasts = inject(Toasts);
  private subscription?: Subscription;

  readonly items = signal<BackgroundTask[]>([]);
  /** Task ids whose cancel or dismiss is being sent, to disable the button meanwhile. */
  readonly busy = signal<ReadonlySet<string>>(new Set());
  readonly active = computed(() => this.items().filter(isActive).length);
  readonly finished = computed(() => this.items().length - this.active());
  readonly hasFailures = computed(() => this.items().some(t => t.state === 'Failed'));

  start() {
    if (this.subscription) return;
    this.subscription = this.events.backgroundTasksUpdated.subscribe(() => this.refresh());
    this.subscription.add(this.events.backgroundTaskProgress.subscribe(p => this.applyProgress(p)));
    this.refresh();
  }

  stop() {
    this.subscription?.unsubscribe();
    this.subscription = undefined;
    this.items.set([]);
  }

  async refresh() {
    try {
      this.items.set(await this.api.backgroundTasks());
    } catch {
      // Keep the last known state; not worth an error toast.
    }
  }

  /** Updates a task in place; ones not loaded yet are left alone (the list refetch brings them). */
  private applyProgress(update: BackgroundTaskProgress) {
    this.items.update(items => items.map(t => t.id !== update.id || !isActive(t) ? t : {
      ...t,
      message: update.message ?? t.message,
      percent: update.percent ?? null,
      remainingSeconds: update.remainingSeconds ?? null,
    }));
  }

  /** Cancels a waiting or running task; the server publishes the change, so the list refreshes by itself. */
  async cancel(task: BackgroundTask) {
    await this.run(task.id, () => this.api.cancelBackgroundTask(task.id));
  }

  /** Hides a finished task. */
  async dismiss(task: BackgroundTask) {
    this.items.update(items => items.filter(t => t.id !== task.id));
    await this.run(task.id, () => this.api.dismissBackgroundTask(task.id));
  }

  /** Hides every finished task. */
  async dismissFinished() {
    this.items.update(items => items.filter(isActive));
    try {
      await this.api.dismissFinishedBackgroundTasks();
    } catch (e) {
      this.toasts.error(errorMessage(e));
      await this.refresh();
    }
  }

  private async run(id: string, call: () => Promise<void>) {
    this.busy.update(ids => new Set(ids).add(id));
    try {
      await call();
    } catch (e) {
      this.toasts.error(errorMessage(e));
      await this.refresh();
    } finally {
      this.busy.update(ids => { const next = new Set(ids); next.delete(id); return next; });
    }
  }
}
