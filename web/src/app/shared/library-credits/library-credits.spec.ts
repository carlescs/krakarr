import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { LibraryCredits } from './library-credits';

async function render(kind: 'movie' | 'series', api = mockApi()) {
  setupTest({ api });
  const fixture = TestBed.createComponent(LibraryCredits);
  fixture.componentRef.setInput('kind', kind);
  fixture.componentRef.setInput('id', 3);
  await settle(fixture);
  return fixture.nativeElement as HTMLElement;
}

describe('LibraryCredits', () => {
  it('loads movie credits', async () => {
    const api = mockApi({ movieCredits: async () => ({ cast: [{ tmdbId: 1, name: 'Amy Adams' }], crew: [] }) });
    const el = await render('movie', api);
    expect(api.movieCredits).toHaveBeenCalledWith(3);
    expect(text(el)).toContain('Amy Adams');
  });

  it('loads series credits', async () => {
    const api = mockApi({ seriesCredits: async () => ({ cast: [], crew: [{ tmdbId: 2, name: 'Dan Erickson', jobs: 'Creator' }] }) });
    const el = await render('series', api);
    expect(api.seriesCredits).toHaveBeenCalledWith(3);
    expect(text(el)).toContain('Dan Erickson');
  });

  it('hides a missing TMDB key but shows other errors', async () => {
    expect(text(await render('movie', mockApi({ movieCredits: async () => { throw new Error('TMDB API key missing'); } })))).toBe('');
    TestBed.resetTestingModule();
    expect(text(await render('movie', mockApi({ movieCredits: async () => { throw new Error('TMDB is down'); } })))).toBe('TMDB is down');
  });
});
