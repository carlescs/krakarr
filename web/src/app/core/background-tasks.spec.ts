import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from './api';
import { BackgroundTasks } from './background-tasks';
import { Events } from './events';
import { BackgroundTask, BackgroundTaskProgress } from './models';
import { Toasts } from './toast';

const task = (id: string, state: BackgroundTask['state'], extra: Partial<BackgroundTask> = {}): BackgroundTask =>
  ({ id, kind: 'subtitle', title: `Sync ${id}`, link: '/movies/1', state, queued: '2026-11-26T10:00:00Z', canCancel: state === 'Waiting' || state === 'Running', ...extra });

function setup(api: Record<string, unknown>) {
  const events = { backgroundTasksUpdated: new Subject<void>(), backgroundTaskProgress: new Subject<BackgroundTaskProgress>() };
  const toasts = { error: vi.fn() };
  TestBed.configureTestingModule({
    providers: [{ provide: Api, useValue: api }, { provide: Events, useValue: events }, { provide: Toasts, useValue: toasts }],
  });
  return { tasks: TestBed.inject(BackgroundTasks), events, toasts };
}

describe('BackgroundTasks', () => {
  it('loads tasks, refetches when they change and stops cleanly', async () => {
    const responses = [[task('a', 'Running')], [task('a', 'Succeeded'), task('b', 'Failed')]];
    const api = { backgroundTasks: vi.fn(async () => responses.shift() ?? []) };
    const { tasks, events } = setup(api);

    tasks.start();
    await vi.waitFor(() => expect(tasks.active()).toBe(1));
    expect(tasks.finished()).toBe(0);

    events.backgroundTasksUpdated.next();
    await vi.waitFor(() => expect(tasks.finished()).toBe(2));
    expect(tasks.active()).toBe(0);
    expect(tasks.hasFailures()).toBe(true);

    tasks.stop();
    events.backgroundTasksUpdated.next();
    expect(api.backgroundTasks).toHaveBeenCalledTimes(2);
    expect(tasks.items()).toEqual([]);
  });

  it('applies progress in place to running tasks only', async () => {
    const api = { backgroundTasks: vi.fn(async () => [task('a', 'Running', { message: 'Looking for a reference' }), task('b', 'Succeeded', { percent: 100 })]) };
    const { tasks, events } = setup(api);
    tasks.start();
    await vi.waitFor(() => expect(tasks.items().length).toBe(2));

    events.backgroundTaskProgress.next({ id: 'a', percent: 42, message: 'Syncing with alass', remainingSeconds: 95 });
    events.backgroundTaskProgress.next({ id: 'b', percent: 10, message: 'late' });
    events.backgroundTaskProgress.next({ id: 'c', percent: 10 });

    const [a, b] = tasks.items();
    expect(a).toMatchObject({ percent: 42, message: 'Syncing with alass', remainingSeconds: 95 });
    expect(b).toMatchObject({ percent: 100, state: 'Succeeded' });
    expect(api.backgroundTasks).toHaveBeenCalledTimes(1);
    tasks.stop();
  });

  it('cancels, dismisses and clears finished tasks, reloading on failure', async () => {
    const api = {
      backgroundTasks: vi.fn(async () => [task('a', 'Running'), task('b', 'Succeeded'), task('c', 'Failed')]),
      cancelBackgroundTask: vi.fn(async () => undefined),
      dismissBackgroundTask: vi.fn(async () => undefined),
      dismissFinishedBackgroundTasks: vi.fn(async () => undefined),
    };
    const { tasks, toasts } = setup(api);
    await tasks.refresh();

    await tasks.cancel(tasks.items()[0]);
    expect(api.cancelBackgroundTask).toHaveBeenCalledWith('a');
    expect(tasks.busy().size).toBe(0);
    expect(api.backgroundTasks).toHaveBeenCalledTimes(1); // the server's event refreshes the list

    await tasks.dismiss(tasks.items()[1]);
    expect(api.dismissBackgroundTask).toHaveBeenCalledWith('b');
    expect(tasks.items().map(t => t.id)).toEqual(['a', 'c']);

    await tasks.dismissFinished();
    expect(api.dismissFinishedBackgroundTasks).toHaveBeenCalled();
    expect(tasks.items().map(t => t.id)).toEqual(['a']);

    api.cancelBackgroundTask.mockRejectedValueOnce(new Error('Not found'));
    await tasks.cancel(tasks.items()[0]);
    expect(toasts.error).toHaveBeenCalled();
    expect(api.backgroundTasks).toHaveBeenCalledTimes(2);
    expect(tasks.busy().size).toBe(0);
  });
});
