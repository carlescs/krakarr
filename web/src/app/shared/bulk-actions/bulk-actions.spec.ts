import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MediaType, UserRole } from '../../core/models';
import { Selection } from '../../core/selection';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { BulkActions } from './bulk-actions';

@Component({
  imports: [BulkActions],
  template: '<kr-bulk-actions [mediaType]="mediaType" [selection]="selection" [total]="3" />',
})
class Host {
  mediaType: MediaType = 'Movie';
  readonly selection = new Selection(signal([{ id: 1 }, { id: 2 }, { id: 3 }]));
}

async function render(role: UserRole = 'Admin', mediaType: MediaType = 'Movie') {
  const api = setupTest({
    role,
    api: mockApi({
      bulkSubscribe: async () => ({ affected: 2, errors: [] }),
      subscriptions: async () => [],
      bulkEdit: async () => ({ affected: 2, errors: [] }),
      bulkSearch: async () => ({ id: 'c', name: 'Search: 2 movie(s)', status: 'Queued', queued: '' }),
      bulkDelete: async () => ({ affected: 2, errors: [] }),
      qualityProfiles: async () => [{ id: 4, name: 'HD' }],
      movies: async () => [],
      movieCollections: async () => [],
      seriesList: async () => [],
    }),
  });
  const fixture = TestBed.createComponent(Host);
  fixture.componentInstance.mediaType = mediaType;
  fixture.componentInstance.selection.toggle(1);
  fixture.componentInstance.selection.toggle(3);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('BulkActions', () => {
  afterEach(() => vi.restoreAllMocks());

  it('follows and unfollows the selection', async () => {
    const { api, fixture, el } = await render();
    expect(text(el)).toContain('2 selected');
    button(el, 'Follow').click();
    await settle(fixture);
    expect(api.bulkSubscribe).toHaveBeenCalledWith('Movie', [1, 3], true);
    button(el, 'Unfollow').click();
    await settle(fixture);
    expect(api.bulkSubscribe).toHaveBeenCalledWith('Movie', [1, 3], false);
  });

  it('only offers following to plain users', async () => {
    const { el } = await render('User');
    expect(() => button(el, 'Monitor')).toThrow();
    expect(button(el, 'Follow')).toBeTruthy();
  });

  it('monitors, searches and changes the quality profile', async () => {
    const { api, fixture, el } = await render('Admin', 'Series');
    button(el, 'Unmonitor').click();
    await settle(fixture);
    expect(api.bulkEdit).toHaveBeenCalledWith('Series', [1, 3], { monitored: false });
    expect(api.seriesList).toHaveBeenCalled();

    button(el, 'Search missing').click();
    await settle(fixture);
    expect(api.bulkSearch).toHaveBeenCalledWith('Series', [1, 3]);

    button(el, 'More bulk actions').click();
    await settle(fixture);
    button(el, 'Change quality profile').click();
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.bulkEdit).toHaveBeenCalledWith('Series', [1, 3], { qualityProfileId: 4 });
  });

  it('deletes after confirming, optionally with files', async () => {
    const { api, fixture, el } = await render();
    button(el, 'More bulk actions').click();
    await settle(fixture);
    button(el, 'Delete…').click();
    await settle(fixture);
    expect(text(el, '[role="dialog"]')).toContain('Delete 2 movies');
    (el.querySelector('[role="dialog"] input[type="checkbox"]') as HTMLInputElement).click();
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Delete').click();
    await settle(fixture);
    expect(api.bulkDelete).toHaveBeenCalledWith('Movie', [1, 3], true);
    expect(el.querySelector('[role="dialog"]')).toBeNull();
  });
});
