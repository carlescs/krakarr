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

  it('shows a progress bar for work in progress', async () => {
    setupTest();
    TestBed.inject(Notifications).items.set([
      { id: 'subtitles.sync.1', kind: 'subtitle', title: 'Syncing Dune (2021) · English', message: 'Syncing with alass against the audio',
        count: 1, severity: 'Info', link: '/movies/3', progress: { percent: 42, remainingSeconds: 150 } },
      { id: 'subtitles.sync.2', kind: 'subtitle', title: 'Waiting to sync Severance S01E03 · Spanish', message: 'Waiting for its turn',
        count: 1, severity: 'Info', link: '/series/5', progress: { percent: null } },
    ] as AppNotification[]);
    const fixture = TestBed.createComponent(NotificationBell);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Notifications (2)').click();
    await settle(fixture);

    const bars = el.querySelectorAll<HTMLElement>('[role="progressbar"]');
    expect(bars.length).toBe(2);
    expect(bars[0].getAttribute('aria-valuenow')).toBe('42');
    expect((bars[0].firstElementChild as HTMLElement).style.width).toBe('42%');
    expect(text(el)).toContain('42% · 3 min left');
    expect(bars[1].getAttribute('aria-valuenow')).toBeNull();
    expect(bars[1].querySelector('.animate-pulse')).not.toBeNull();
  });

  it('shows a finished sync in green', async () => {
    setupTest();
    TestBed.inject(Notifications).items.set([
      { id: 'subtitles.sync.1', kind: 'subtitle', title: 'Synced Dune (2021) · English', message: 'Synced with alass against the audio',
        count: 1, severity: 'Success', link: '/movies/3', progress: { percent: 100 } },
    ] as AppNotification[]);
    const fixture = TestBed.createComponent(NotificationBell);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const bell = button(el, 'Notifications (1)');
    expect(bell.querySelector('.bg-rose-400')).toBeNull();
    bell.click();
    await settle(fixture);

    const item = el.querySelector('li')!;
    expect(item.classList).toContain('bg-emerald-500/10');
    expect(item.querySelector('svg')!.classList).toContain('text-emerald-400');
    expect(item.querySelector('.text-emerald-300')!.textContent).toContain('Synced Dune (2021) · English');
    expect(item.querySelector('[role="progressbar"] .bg-emerald-400')).not.toBeNull();
    expect(text(item)).toContain('100%');
  });

  it('formats the time left', async () => {
    setupTest();
    const items = TestBed.inject(Notifications).items;
    const fixture = TestBed.createComponent(NotificationBell);
    const el = fixture.nativeElement as HTMLElement;
    const show = async (remainingSeconds: number | null) => {
      items.set([{ id: 'subtitles.sync.1', kind: 'subtitle', title: 'Syncing', count: 1, severity: 'Info', link: '/movies/3',
        progress: { percent: 10, remainingSeconds } }] as AppNotification[]);
      await settle(fixture);
      return text(el.querySelector('[role="progressbar"]')!.parentElement as HTMLElement);
    };
    await settle(fixture);
    button(el, 'Notifications').click();

    expect(await show(null)).toBe('10%');
    expect(await show(0)).toBe('10% · 1 s left');
    expect(await show(44.2)).toBe('10% · 45 s left');
    expect(await show(61)).toBe('10% · 2 min left');
    expect(await show(3600)).toBe('10% · 1 h left');
    expect(await show(3900)).toBe('10% · 1 h 5 min left');
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
