import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { PlayState } from '../../core/models';
import { settle, setupTest } from '../../../testing/test-setup';
import { PlayStatus } from './play-status';

async function render(states: PlayState[]) {
  setupTest();
  const fixture = TestBed.createComponent(PlayStatus);
  fixture.componentRef.setInput('states', states);
  await settle(fixture);
  return fixture.nativeElement as HTMLElement;
}

const badges = (el: HTMLElement) => [...el.querySelectorAll('[data-play-status]')].map(b => b.textContent?.replace(/\s+/g, ' ').trim());

describe('PlayStatus', () => {
  it('shows a short badge when a single server reports', async () => {
    const el = await render([{ server: 'Jellyfin', played: true, playCount: 2, lastPlayed: '2026-11-01T20:00:00Z' }]);
    expect(badges(el)).toEqual(['Watched']);
    expect(el.querySelector('[data-play-status]')?.getAttribute('title')).toContain('Jellyfin: watched 2 times, last on');
  });

  it('names each server when several report', async () => {
    const el = await render([
      { server: 'Jellyfin', played: false, playCount: 0, progress: 0.4 },
      { server: 'Plex', played: false, playCount: 0 },
    ]);
    expect(badges(el)).toEqual(['40% watched · Jellyfin', 'Unwatched · Plex']);
  });
});
