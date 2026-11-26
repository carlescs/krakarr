import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from './api';
import { Events } from './events';
import { AppNotification, NotificationProgressUpdate } from './models';
import { Notifications } from './notifications';
import { Toasts } from './toast';

const note = (id: string, count: number, severity: AppNotification['severity'] = 'Info'): AppNotification =>
  ({ id, kind: 'request', title: id, link: '/requests', count, severity });

describe('Notifications', () => {
  it('sums counts and refetches when a source changes', async () => {
    const responses = [[note('requests.pending', 2)], [note('requests.pending', 1), note('health.disk', 1, 'Warning')]];
    const api = { notifications: vi.fn(async () => responses.shift() ?? []) };
    const events = { notificationsUpdated: new Subject<void>(), requestsUpdated: new Subject<void>(), notificationProgress: new Subject<NotificationProgressUpdate>() };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }, { provide: Events, useValue: events }] });
    const notifications = TestBed.inject(Notifications);

    notifications.start();
    await vi.waitFor(() => expect(notifications.tray('bell').total()).toBe(2));
    expect(notifications.count('requests.pending')).toBe(2);
    expect(notifications.tray('bell').hasProblems()).toBe(false);

    events.notificationsUpdated.next();
    await vi.waitFor(() => expect(notifications.items().length).toBe(2));
    expect(notifications.tray('bell').total()).toBe(2);
    expect(notifications.count('requests.pending')).toBe(1);
    expect(notifications.tray('bell').hasProblems()).toBe(true);

    notifications.stop();
    events.requestsUpdated.next();
    expect(api.notifications).toHaveBeenCalledTimes(2);
    expect(notifications.tray('bell').total()).toBe(0);
  });

  it('splits notifications by tray and dismisses one tray at a time', async () => {
    const sync = (id: string, severity: AppNotification['severity'], progress: AppNotification['progress']): AppNotification =>
      ({ id, kind: 'subtitle', title: id, link: '/movies/1', count: 1, severity, progress, tray: 'subtitleSync' });
    const api = {
      notifications: vi.fn(async () => [note('requests.pending', 2), sync('subtitles.sync.1', 'Info', { percent: 5 }), sync('subtitles.sync.2', 'Error', null)]),
      dismissAllNotifications: vi.fn(async () => undefined),
    };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }] });
    const notifications = TestBed.inject(Notifications);

    await notifications.refresh();
    expect(notifications.tray('bell').items().map(n => n.id)).toEqual(['requests.pending']);
    expect(notifications.tray('bell').total()).toBe(2);
    expect(notifications.tray('bell').hasProblems()).toBe(false);
    expect(notifications.tray('subtitleSync').total()).toBe(2);
    expect(notifications.tray('subtitleSync').hasProblems()).toBe(true);
    expect(notifications.tray('subtitleSync').busy()).toBe(true);

    await notifications.dismissAll('subtitleSync');
    expect(api.dismissAllNotifications).toHaveBeenCalledWith('subtitleSync');
    expect(notifications.items().map(n => n.id)).toEqual(['requests.pending']);
    expect(notifications.tray('subtitleSync').busy()).toBe(false);
  });

  it('dismisses one or all notifications and reloads on failure', async () => {
    const api = {
      notifications: vi.fn(async () => [note('requests.pending', 2), note('releases.movies', 1)]),
      dismissNotification: vi.fn(async () => undefined),
      dismissAllNotifications: vi.fn(async () => undefined),
    };
    const events = { notificationsUpdated: new Subject<void>(), requestsUpdated: new Subject<void>(), notificationProgress: new Subject<NotificationProgressUpdate>() };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }, { provide: Events, useValue: events }] });
    const notifications = TestBed.inject(Notifications);

    await notifications.refresh();
    await notifications.dismiss('requests.pending');
    expect(api.dismissNotification).toHaveBeenCalledWith('requests.pending');
    expect(notifications.items().map(n => n.id)).toEqual(['releases.movies']);

    await notifications.dismissAll();
    expect(api.dismissAllNotifications).toHaveBeenCalledWith('bell');
    expect(notifications.items()).toEqual([]);

    api.dismissNotification.mockRejectedValueOnce(new Error('boom'));
    await notifications.dismiss('releases.movies');
    expect(notifications.items().length).toBe(2);
  });

  it('updates the progress of work in place without refetching', async () => {
    const sync: AppNotification = { ...note('subtitles.sync.7', 1), kind: 'subtitle', message: 'Looking for a reference', progress: { percent: null } };
    const api = { notifications: vi.fn(async () => [sync, note('requests.pending', 1)]) };
    const events = { notificationsUpdated: new Subject<void>(), requestsUpdated: new Subject<void>(), notificationProgress: new Subject<NotificationProgressUpdate>() };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }, { provide: Events, useValue: events }] });
    const notifications = TestBed.inject(Notifications);

    notifications.start();
    await vi.waitFor(() => expect(notifications.items().length).toBe(2));
    events.notificationProgress.next({ id: 'subtitles.sync.7', percent: 42, message: 'Syncing with alass against the audio', remainingSeconds: 95 });
    events.notificationProgress.next({ id: 'subtitles.sync.9', percent: 10, message: 'Not shown' });

    const [updated, other] = notifications.items();
    expect(updated.progress).toEqual({ percent: 42, remainingSeconds: 95 });
    expect(updated.message).toBe('Syncing with alass against the audio');
    expect(other.progress).toBeUndefined();
    expect(notifications.items().length).toBe(2);
    expect(api.notifications).toHaveBeenCalledTimes(1);
    notifications.stop();
  });

  it('runs a notification button and reports a failure', async () => {
    const action = { label: 'Cancel', path: 'subtitles/7/sync/cancel' };
    const sync: AppNotification = { ...note('subtitles.sync.7', 1), actions: [action] };
    const api = {
      notifications: vi.fn(async () => [sync]),
      notificationAction: vi.fn(async () => undefined),
    };
    const toasts = { error: vi.fn() };
    TestBed.configureTestingModule({ providers: [{ provide: Api, useValue: api }, { provide: Toasts, useValue: toasts }] });
    const notifications = TestBed.inject(Notifications);

    await notifications.act(sync, action);
    expect(api.notificationAction).toHaveBeenCalledWith('subtitles/7/sync/cancel');
    expect(api.notifications).not.toHaveBeenCalled(); // the server's event refreshes the bell
    expect(notifications.busy().size).toBe(0);

    api.notificationAction.mockRejectedValueOnce(new Error('Not found'));
    await notifications.act(sync, action);
    expect(toasts.error).toHaveBeenCalled();
    expect(api.notifications).toHaveBeenCalledTimes(1);
    expect(notifications.busy().size).toBe(0);
  });
});
