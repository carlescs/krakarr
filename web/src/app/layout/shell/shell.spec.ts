import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { Events } from '../../core/events';
import { Notifications } from '../../core/notifications';
import { TorrentStatus, UserRole } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { Shell } from './shell';

async function render(role: UserRole, api = mockApi()) {
  setupTest({ role, api });
  const events = TestBed.inject(Events);
  vi.spyOn(events, 'start').mockResolvedValue();
  vi.spyOn(events, 'stop').mockResolvedValue();
  const fixture = TestBed.createComponent(Shell);
  await settle(fixture);
  return { fixture, events, el: fixture.nativeElement as HTMLElement };
}

const navLabels = (el: HTMLElement) => [...el.querySelectorAll('aside nav a')].map(a => text(a as HTMLElement));

describe('Shell', () => {
  it('shows admins every section', async () => {
    const { el, events } = await render('Admin');
    expect(events.start).toHaveBeenCalled();
    expect(navLabels(el)).toEqual(['Dashboard', 'Discover', 'Movies', 'Series', 'Calendar', 'Requests', 'Subtitles', 'Search', 'Indexers', 'Downloads', 'Activity', 'Settings']);
  });

  it('hides admin and staff pages from plain users', async () => {
    const { el } = await render('User');
    expect(navLabels(el)).toEqual(['Discover', 'Movies', 'Series', 'Calendar', 'Requests', 'Account']);
    expect(text(el)).not.toContain('↓');
  });

  it('badges active downloads and pending requests', async () => {
    const { fixture, el, events } = await render('Manager');
    events.downloads.set([
      { state: 'Downloading', downloadRate: 1024, uploadRate: 0 },
      { state: 'Seeding', downloadRate: 0, uploadRate: 2048 },
    ] as TorrentStatus[]);
    TestBed.inject(Notifications).items.set([{ id: 'requests.pending', kind: 'request', title: 'Requests', count: 4, severity: 'Info', link: '/requests' }]);
    await settle(fixture);
    const nav = navLabels(el);
    expect(nav).toContain('Downloads1');
    expect(nav).toContain('Requests4');
  });

  for (const role of ['Admin', 'User'] as UserRole[]) {
    it(`links ${role.toLowerCase()}s to search from the top bar`, async () => {
      const { el } = await render(role);
      expect(el.querySelector('header a[aria-label="Search"]')?.getAttribute('href')).toBe('/discover/search');
    });
  }

  it('signs out', async () => {
    const api = mockApi({ logout: async () => undefined });
    const { fixture, el, events } = await render('Admin', api);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    button(el, 'Sign out').click();
    await settle(fixture);
    expect(api.logout).toHaveBeenCalled();
    expect(events.stop).toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith('/login');
  });
});
