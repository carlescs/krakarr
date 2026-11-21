import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { CommandInfo } from '../../core/models';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { ActivityPage } from './activity';

describe('ActivityPage', () => {
  it('lists history and tasks and follows live updates', async () => {
    const api = setupTest({
      api: mockApi({
        history: async () => ({ items: [
          { id: 1, eventType: 'Imported', sourceTitle: 'Alien.1979.1080p', movieId: 3, quality: 'Bluray-1080p', date: '2026-01-01T00:00:00Z' },
          { id: 2, eventType: 'DownloadFailed', sourceTitle: 'Severance.S01E01', seriesId: 4, message: 'Stalled', date: '2026-01-01T00:00:00Z' },
        ] }),
        commands: async () => [{ id: 'c1', name: 'RSS sync', status: 'Running', queued: '2026-01-01T00:00:00Z' }],
      }),
    });
    const fixture = TestBed.createComponent(ActivityPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(api.history).toHaveBeenCalledWith(undefined, 1, 100);
    expect(el.querySelector('a[href="/movies/3"]')?.textContent).toContain('Alien.1979.1080p');
    expect(el.querySelector('a[href="/series/4"]')).not.toBeNull();
    expect(text(el)).toContain('Failed');
    expect(text(el)).toContain('Stalled');
    expect(text(el)).toContain('RSS sync');

    TestBed.inject(Events).commandUpdated.next({ id: 'c2', name: 'Refresh movies', status: 'Queued', queued: '2026-01-01T00:00:00Z' } as CommandInfo);
    await settle(fixture);
    expect(text(el)).toContain('Refresh movies');
  });

  it('shows empty states', async () => {
    setupTest({ api: mockApi({ history: async () => ({ items: [] }), commands: async () => [] }) });
    const fixture = TestBed.createComponent(ActivityPage);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No history yet.');
    expect(text(fixture.nativeElement)).toContain('No recent tasks.');
  });
});
