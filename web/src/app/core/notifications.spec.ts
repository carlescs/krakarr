import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Api } from './api';
import { Events } from './events';
import { AppNotification } from './models';
import { Notifications } from './notifications';

const note = (id: string, count: number, severity: AppNotification['severity'] = 'Info'): AppNotification =>
  ({ id, kind: 'request', title: id, link: '/requests', count, severity });

describe('Notifications', () => {
  it('sums counts and refetches when a source changes', async () => {
    const responses = [[note('requests.pending', 2)], [note('requests.pending', 1), note('health.disk', 1, 'Warning')]];
    const api = { notifications: vi.fn(async () => responses.shift() ?? []) };
    const events = { notificationsUpdated: new Subject<void>(), requestsUpdated: new Subject<void>() };
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
});
