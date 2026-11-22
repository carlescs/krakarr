import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { Toasts } from '../../core/toast';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { AddSeriesModal } from './add-series-modal';

const folders = [
  { id: 1, path: '/movies', mediaType: 'Movie', accessible: true },
  { id: 2, path: '/tv', mediaType: 'Series', accessible: true },
];

describe('AddSeriesModal', () => {
  it('adds the show with the first series root folder', async () => {
    const api = setupTest({
      api: mockApi({
        rootFolders: async () => folders,
        qualityProfiles: async () => [{ id: 4, name: 'Any' }],
        addSeries: async () => ({ id: 9, title: 'Severance' }),
      }),
    });
    const fixture = TestBed.createComponent(AddSeriesModal);
    fixture.componentRef.setInput('show', { tmdbId: 95396, tvdbId: 371980, title: 'Severance' });
    const added = vi.fn();
    fixture.componentInstance.added.subscribe(added);
    await settle(fixture);
    expect(text(fixture.nativeElement, 'h2')).toBe('Add Severance');

    button(fixture.nativeElement, 'Add series').click();
    await settle(fixture);
    expect(api.addSeries).toHaveBeenCalledWith({
      tmdbId: 95396, tvdbId: 371980, qualityProfileId: 4, rootFolderPath: '/tv', monitor: 'All', seasonFolder: true, searchForMissing: true,
    });
    expect(added).toHaveBeenCalledWith({ id: 9, title: 'Severance' });
  });

  it('reports errors and stays open', async () => {
    setupTest({
      api: mockApi({
        rootFolders: async () => folders,
        qualityProfiles: async () => [{ id: 4, name: 'Any' }],
        addSeries: async () => { throw new Error('Already in the library'); },
      }),
    });
    const toasts = TestBed.inject(Toasts);
    const fixture = TestBed.createComponent(AddSeriesModal);
    fixture.componentRef.setInput('show', { tmdbId: 1, title: 'Severance' });
    await settle(fixture);
    button(fixture.nativeElement, 'Add series').click();
    await settle(fixture);
    expect(toasts.items().map(t => t.message)).toEqual(['Already in the library']);
    expect(button(fixture.nativeElement, 'Add series').disabled).toBe(false);
  });

  it('adds the show under an edited title to tell same-named shows apart', async () => {
    const api = setupTest({
      api: mockApi({
        rootFolders: async () => folders,
        qualityProfiles: async () => [{ id: 4, name: 'Any' }],
        addSeries: async () => ({ id: 10, title: 'The Office (US)' }),
      }),
    });
    const fixture = TestBed.createComponent(AddSeriesModal);
    fixture.componentRef.setInput('show', { tmdbId: 2316, title: 'The Office' });
    await settle(fixture);
    const input = fixture.nativeElement.querySelector('#add-series-title') as HTMLInputElement;
    expect(input.value).toBe('The Office');
    input.value = ' The Office (US) ';
    input.dispatchEvent(new Event('input'));
    button(fixture.nativeElement, 'Add series').click();
    await settle(fixture);
    expect(api.addSeries).toHaveBeenCalledWith(expect.objectContaining({ tmdbId: 2316, title: 'The Office (US)' }));
  });
});
