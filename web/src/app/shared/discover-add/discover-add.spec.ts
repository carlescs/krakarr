import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { UserRole } from '../../core/models';
import { settle, setupTest } from '../../../testing/test-setup';
import { DiscoverAdd } from './discover-add';

async function render(role: UserRole, kind: 'movie' | 'series') {
  setupTest({ role });
  const fixture = TestBed.createComponent(DiscoverAdd);
  fixture.componentRef.setInput('item', { kind, item: { tmdbId: 1, title: 'Arrival', year: 2016 } });
  await settle(fixture);
  return fixture.nativeElement as HTMLElement;
}

describe('DiscoverAdd', () => {
  it('opens the add movie dialog for staff', async () => {
    expect((await render('Manager', 'movie')).querySelector('kr-add-movie-modal')).not.toBeNull();
  });

  it('opens the add series dialog for staff', async () => {
    expect((await render('Admin', 'series')).querySelector('kr-add-series-modal')).not.toBeNull();
  });

  it('opens the request dialog for plain users', async () => {
    const el = await render('User', 'movie');
    expect(el.querySelector('kr-request-modal')).not.toBeNull();
    expect(el.querySelector('kr-add-movie-modal')).toBeNull();
  });
});
