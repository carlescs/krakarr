import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { MockApi, button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { LibraryImportPage } from './library-import';

const folders = [
  { id: 1, path: '/movies', mediaType: 'Movie', accessible: true },
  { id: 2, path: '/tv', mediaType: 'Series', accessible: true },
];
const candidates = [
  { folderName: 'Alien (1979)', path: '/movies/Alien (1979)', parsedTitle: 'Alien', parsedYear: 1979, confidence: 'Exact', alreadyInLibrary: false,
    match: { tmdbId: 348, title: 'Alien', year: 1979 }, videoFile: { quality: 'Bluray-1080p', size: 1073741824 } },
  { folderName: 'Weird Folder', path: '/movies/Weird Folder', parsedTitle: 'Weird Folder', confidence: 'None', alreadyInLibrary: false },
  { folderName: 'Arrival', path: '/movies/Arrival', parsedTitle: 'Arrival', confidence: 'High', alreadyInLibrary: true, match: { tmdbId: 1, title: 'Arrival', year: 2016 } },
];

async function render(kind: 'movies' | 'series', api: MockApi) {
  setupTest({ api });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(LibraryImportPage);
  fixture.componentRef.setInput('kind', kind);
  await settle(fixture);
  return { fixture, navigate, el: fixture.nativeElement as HTMLElement };
}

describe('LibraryImportPage', () => {
  it('scans a movie folder, preselects good matches and imports them', async () => {
    const api = mockApi({
      rootFolders: async () => folders, qualityProfiles: async () => [{ id: 7, name: 'HD' }],
      scanMovieImport: async () => candidates, importMovies: async () => undefined,
    });
    const { fixture, navigate, el } = await render('movies', api);
    expect(text(el, 'h1')).toBe('Import movie library');
    button(el, 'Scan folder').click();
    await settle(fixture);
    expect(api.scanMovieImport).toHaveBeenCalledWith(1);
    expect(text(el)).toContain('Bluray-1080p · 1.0 GB');
    expect(text(el)).toContain('No match');
    expect(button(el, 'Matched').textContent).toContain('1');

    button(el, 'Import 1 selected').click();
    await settle(fixture);
    expect(api.importMovies).toHaveBeenCalledWith([
      { path: '/movies/Alien (1979)', tmdbId: 348, qualityProfileId: 7, monitored: true, searchForMovie: false, minimumAvailability: 'Released' },
    ]);
    expect(navigate).toHaveBeenCalledWith('/movies');
  });

  it('matches an unmatched folder by hand', async () => {
    const api = mockApi({
      rootFolders: async () => folders, qualityProfiles: async () => [{ id: 7, name: 'HD' }],
      scanMovieImport: async () => candidates, lookupMovies: async () => [{ tmdbId: 99, title: 'The Weird One', year: 2001 }],
    });
    const { fixture, el } = await render('movies', api);
    button(el, 'Scan folder').click();
    await settle(fixture);
    button(el, 'Unmatched').click();
    await settle(fixture);
    button(el, 'Change match').click();
    await settle(fixture);
    expect(api.lookupMovies).toHaveBeenCalledWith('Weird Folder');
    button(el, 'The Weird One').click();
    await settle(fixture);
    expect(el.querySelector('[role="dialog"]')).toBeNull();
    expect(text(el)).toContain('Import 2 selected');
  });

  it('asks for a series root folder first', async () => {
    const { el } = await render('series', mockApi({ rootFolders: async () => [folders[0]], qualityProfiles: async () => [] }));
    expect(text(el, 'h1')).toBe('Import series library');
    expect(text(el)).toContain('Add a root folder of type “Series”');
  });
});
