import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from './api';
import { Events } from './events';
import { AppNotification, NotificationProgressUpdate } from './models';
import { Notifications } from './notifications';

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
    await vi.waitFor(() => expect(notifications.total()).toBe(2));
    expect(notifications.count('requests.pending')).toBe(2);
    expect(notifications.hasProblems()).toBe(false);

    events.notificationsUpdated.next();
    await vi.waitFor(() => expect(notifications.items().length).toBe(2));
    expect(notifications.total()).toBe(2);
    expect(notifications.count('requests.pending')).toBe(1);
    expect(notifications.hasProblems()).toBe(true);

    notifications.stop();
    events.requestsUpdated.next();
    expect(api.notifications).toHaveBeenCalledTimes(2);
    expect(notifications.total()).toBe(0);
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
    expect(api.dismissAllNotifications).toHaveBeenCalled();
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
});
