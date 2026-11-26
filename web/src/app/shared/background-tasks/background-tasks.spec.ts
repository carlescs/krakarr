import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { BackgroundTasks } from '../../core/background-tasks';
import { BackgroundTask } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { BackgroundTasksMenu } from './background-tasks';

const task = (id: string, state: BackgroundTask['state'], extra: Partial<BackgroundTask> = {}): BackgroundTask =>
  ({ id, kind: 'subtitle', title: `Sync ${id}`, link: '/movies/3', state, queued: '2026-11-26T10:00:00Z', canCancel: state === 'Waiting' || state === 'Running', ...extra });

async function render(items: BackgroundTask[]) {
  TestBed.inject(BackgroundTasks).items.set(items);
  const fixture = TestBed.createComponent(BackgroundTasksMenu);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

describe('BackgroundTasksMenu', () => {
  it('stays hidden while there is nothing to show', async () => {
    setupTest();
    const { el, fixture } = await render([]);
    expect(el.classList).toContain('hidden');

    TestBed.inject(BackgroundTasks).items.set([task('a', 'Waiting')]);
    await settle(fixture);
    expect(el.classList).not.toContain('hidden');
  });

  it('spins and counts running tasks, and lists them with their progress', async () => {
    setupTest();
    const { el, fixture } = await render([
      task('Dune (2021) · English', 'Running', { message: 'Syncing with alass against the audio', percent: 42, remainingSeconds: 150 }),
      task('Severance S01E03 · Spanish', 'Waiting'),
    ]);
    const icon = button(el, 'Tasks (2 running)');
    expect(icon.querySelector('svg')!.classList).toContain('animate-spin');
    expect(text(icon)).toBe('2');
    expect(icon.querySelector('.bg-accent-400')).not.toBeNull();

    icon.click();
    await settle(fixture);
    expect(text(el)).toContain('Sync Dune (2021) · English');
    expect(text(el)).toContain('Syncing with alass against the audio');
    expect(text(el)).toContain('Waiting for its turn');
    expect(el.querySelector('a[href="/movies/3"]')).not.toBeNull();

    const bars = el.querySelectorAll<HTMLElement>('[role="progressbar"]');
    expect(bars.length).toBe(2);
    expect(bars[0].getAttribute('aria-valuenow')).toBe('42');
    expect((bars[0].firstElementChild as HTMLElement).style.width).toBe('42%');
    expect(text(el)).toContain('42% · 3 min left');
    expect(bars[1].getAttribute('aria-valuenow')).toBeNull();
    expect(bars[1].querySelector('.animate-pulse')).not.toBeNull();
    expect(el.querySelector('[aria-label^="Dismiss"]')).toBeNull(); // running tasks are cancelled, not dismissed
  });

  it('shows outcomes once finished', async () => {
    setupTest();
    const { el, fixture } = await render([
      task('ok', 'Succeeded', { message: 'Synced with alass', percent: 100, finished: '2026-11-26T10:05:00Z' }),
      task('bad', 'Failed', { message: 'The video file was not found', finished: '2026-11-26T10:06:00Z' }),
    ]);
    const icon = button(el, 'Tasks');
    expect(icon.querySelector('svg')!.classList).not.toContain('animate-spin');
    expect(text(icon)).toBe('2');
    expect(icon.querySelector('.bg-rose-400')).not.toBeNull();

    icon.click();
    await settle(fixture);
    const [ok, bad] = Array.from(el.querySelectorAll('li'));
    expect(ok.classList).toContain('bg-emerald-500/10');
    expect(text(ok)).toContain('Done');
    expect(text(bad)).toContain('Failed');
    expect(text(bad)).toContain('The video file was not found');
    expect(el.querySelector('[role="progressbar"]')).toBeNull();
    expect(el.querySelector('[aria-label^="Cancel"]')).toBeNull();
  });

  it('formats the time left', async () => {
    setupTest();
    const { el, fixture } = await render([]);
    const items = TestBed.inject(BackgroundTasks).items;
    const show = async (remainingSeconds: number | null) => {
      items.set([task('a', 'Running', { percent: 10, remainingSeconds })]);
      await settle(fixture);
      return text(el.querySelector('[role="progressbar"]')!.parentElement as HTMLElement);
    };
    items.set([task('a', 'Running')]);
    await settle(fixture);
    button(el, 'Tasks (1 running)').click();

    expect(await show(null)).toBe('10%');
    expect(await show(0)).toBe('10% · 1 s left');
    expect(await show(44.2)).toBe('10% · 45 s left');
    expect(await show(61)).toBe('10% · 2 min left');
    expect(await show(3600)).toBe('10% · 1 h left');
    expect(await show(3900)).toBe('10% · 1 h 5 min left');
  });

  it('cancels, dismisses and clears finished tasks', async () => {
    let finish!: () => void;
    const api = setupTest({
      api: mockApi({
        cancelBackgroundTask: () => new Promise<void>(resolve => (finish = resolve)),
        dismissBackgroundTask: async () => undefined,
        dismissFinishedBackgroundTasks: async () => undefined,
      }),
    });
    const { el, fixture } = await render([task('run', 'Running', { percent: 10 }), task('ok', 'Succeeded'), task('bad', 'Failed')]);
    button(el, 'Tasks (1 running)').click();
    await settle(fixture);

    const cancel = button(el, 'Cancel Sync run');
    cancel.click();
    await settle(fixture);
    expect(api.cancelBackgroundTask).toHaveBeenCalledWith('run');
    expect(cancel.disabled).toBe(true);
    finish();
    await settle(fixture);
    expect(cancel.disabled).toBe(false);

    button(el, 'Dismiss Sync ok').click();
    await settle(fixture);
    expect(api.dismissBackgroundTask).toHaveBeenCalledWith('ok');
    expect(text(el)).not.toContain('Sync ok');

    button(el, 'Clear finished').click();
    await settle(fixture);
    expect(api.dismissFinishedBackgroundTasks).toHaveBeenCalled();
    expect(text(el)).not.toContain('Sync bad');
    expect(text(el)).toContain('Sync run');
  });
});
