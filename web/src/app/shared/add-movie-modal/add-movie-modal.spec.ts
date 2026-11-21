import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { MediaRequest, Movie } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { AddMovieModal } from './add-movie-modal';

const folders = [
  { id: 1, path: '/tv', mediaType: 'Series', accessible: true },
  { id: 2, path: '/movies', mediaType: 'Movie', accessible: true },
];
const profiles = [{ id: 7, name: 'HD' }];

describe('AddMovieModal', () => {
  it('adds the movie with the first movie root folder and profile', async () => {
    const api = setupTest({
      api: mockApi({
        rootFolders: async () => folders,
        qualityProfiles: async () => profiles,
        addMovie: async () => ({ id: 5, title: 'Alien' }),
      }),
    });
    const fixture = TestBed.createComponent(AddMovieModal);
    fixture.componentRef.setInput('movie', { tmdbId: 348, title: 'Alien' });
    const added = vi.fn();
    fixture.componentInstance.added.subscribe(added);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h2')).toBe('Add Alien');

    button(el, 'Add movie').click();
    await settle(fixture);
    expect(api.addMovie).toHaveBeenCalledWith({
      tmdbId: 348, qualityProfileId: 7, rootFolderPath: '/movies', monitored: true, searchForMovie: true, minimumAvailability: 'Released',
    });
    expect(added).toHaveBeenCalledWith({ id: 5, title: 'Alien' } as Movie);
  });

  it('approves a request instead of adding directly', async () => {
    const api = setupTest({
      api: mockApi({
        rootFolders: async () => folders,
        qualityProfiles: async () => profiles,
        approveRequest: async () => ({ id: 3, title: 'Alien' }),
      }),
    });
    const fixture = TestBed.createComponent(AddMovieModal);
    fixture.componentRef.setInput('movie', { tmdbId: 348, title: 'Alien' });
    fixture.componentRef.setInput('request', { id: 3, requestedBy: 'sam' } as MediaRequest);
    const approved = vi.fn();
    fixture.componentInstance.approved.subscribe(approved);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Requested by sam');

    button(fixture.nativeElement, 'Approve and add').click();
    await settle(fixture);
    expect(api.approveRequest).toHaveBeenCalledWith(3, expect.objectContaining({ qualityProfileId: 7, rootFolderPath: '/movies' }));
    expect(api.addMovie).not.toHaveBeenCalled();
    expect(approved).toHaveBeenCalled();
  });

  it('asks for a root folder when there is none', async () => {
    setupTest({ api: mockApi({ rootFolders: async () => [folders[0]], qualityProfiles: async () => profiles }) });
    const fixture = TestBed.createComponent(AddMovieModal);
    fixture.componentRef.setInput('movie', { tmdbId: 348, title: 'Alien' });
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('No root folders configured');
    expect(button(fixture.nativeElement, 'Add movie').disabled).toBe(true);
  });
});
