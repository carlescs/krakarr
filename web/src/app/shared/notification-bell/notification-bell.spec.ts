import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Notifications } from '../../core/notifications';
import { AppNotification } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { NotificationBell } from './notification-bell';

describe('NotificationBell', () => {
  it('shows the total and lists notifications when opened', async () => {
    setupTest();
    TestBed.inject(Notifications).items.set([
      { id: 'requests.pending', kind: 'request', title: '2 pending requests', count: 2, severity: 'Info', link: '/requests' },
      { id: 'indexers', kind: 'bell', title: 'Indexer failing', message: 'NZBGeek', count: 1, severity: 'Error', link: '/indexers' },
    ] as AppNotification[]);
    const fixture = TestBed.createComponent(NotificationBell);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const bell = button(el, 'Notifications (3)');
    expect(text(bell)).toBe('3');
    expect(bell.querySelector('.bg-rose-400')).not.toBeNull();

    bell.click();
    await settle(fixture);
    expect(text(el)).toContain('2 pending requests');
    expect(text(el)).toContain('NZBGeek');
    expect(el.querySelector('a[href="/indexers"]')).not.toBeNull();
  });

  it('says when there is nothing to show', async () => {
    setupTest();
    const fixture = TestBed.createComponent(NotificationBell);
    await settle(fixture);
    button(fixture.nativeElement, 'Notifications').click();
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain("You're all caught up.");
  });

  it('dismisses a single notification or all of them', async () => {
    const api = setupTest({
      api: mockApi({
        dismissNotification: async () => undefined,
        dismissAllNotifications: async () => undefined,
      }),
    });
    TestBed.inject(Notifications).items.set([
      { id: 'requests.pending', kind: 'request', title: '2 pending requests', count: 2, severity: 'Info', link: '/requests' },
      { id: 'releases.movies', kind: 'movie', title: '1 movie released', count: 1, severity: 'Info', link: '/calendar' },
    ] as AppNotification[]);
    const fixture = TestBed.createComponent(NotificationBell);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Notifications (3)').click();
    await settle(fixture);
    button(el, 'Dismiss 2 pending requests').click();
    await settle(fixture);
    expect(api.dismissNotification).toHaveBeenCalledWith('requests.pending');
    expect(text(el)).not.toContain('2 pending requests');
    expect(text(el)).toContain('1 movie released');

    button(el, 'Dismiss all').click();
    await settle(fixture);
    expect(api.dismissAllNotifications).toHaveBeenCalled();
    expect(text(el)).toContain("You're all caught up.");
  });
});
