import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import { ScheduledTaskInfo } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { SettingsPage } from './settings';

const task = (t: Partial<ScheduledTaskInfo>): ScheduledTaskInfo => ({
  id: 'rss', name: 'RSS sync', intervalSeconds: 900, lastRun: null, lastDurationSeconds: null, lastSucceeded: null, lastError: null, nextRun: null, isRunning: false, ...t,
});

describe('SettingsPage', () => {
  it('only shows plain users their account', async () => {
    const api = setupTest({ role: 'User' });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h1')).toBe('Account');
    expect(el.querySelector('kr-account-settings')).not.toBeNull();
    expect(api.settings).not.toHaveBeenCalled();
  });

  it('shows admins every tab and loads the settings', async () => {
    const api = setupTest({ api: mockApi({ rootFolders: async () => [{ id: 1, path: '/data/movies', mediaType: 'Movie', accessible: false }] }) });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h1')).toBe('Settings');
    expect([...el.querySelectorAll('h1 + div button')].map(b => b.textContent)).toEqual([
      'Media management', 'Quality profiles', 'Download client', 'Subtitles', 'Metadata', 'Jellyfin', 'Tasks', 'Users', 'General', 'Account',
    ]);
    expect(api.settings).toHaveBeenCalledWith('media');
    expect(api.settings).toHaveBeenCalledWith('general');

    button(el, 'Users').click();
    await settle(fixture);
    expect(el.querySelector('kr-users-settings')).not.toBeNull();
  });

  it('lists scheduled tasks, runs one and follows live updates', async () => {
    const api = setupTest({
      api: mockApi({
        tasks: async () => [task({ lastRun: new Date(Date.now() - 3 * 60_000).toISOString(), lastSucceeded: false, lastDurationSeconds: 2.5 })],
        runTask: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Tasks').click();
    await settle(fixture);
    expect(text(el, 'tbody')).toContain('RSS sync');
    expect(text(el, 'tbody')).toContain('Every 15m');
    expect(text(el, 'tbody')).toContain('3m ago (failed)');
    expect(text(el, 'tbody')).toContain('2.5s');

    button(el, 'Run now').click();
    await settle(fixture);
    expect(api.runTask).toHaveBeenCalledWith('rss');

    TestBed.inject(Events).taskUpdated.next(task({ isRunning: true }));
    await settle(fixture);
    expect(text(el, 'tbody')).toContain('Running…');
  });
});
