import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { AddMoviePage } from './add-movie';

describe('AddMoviePage', () => {
  it('searches and offers to add titles that are not in the library', async () => {
    const api = setupTest({ api: mockApi({ lookupMovies: async () => [{ tmdbId: 603, title: 'The Matrix', year: 1999, originalTitle: 'Matrix', runtime: 136 }, { tmdbId: 604, title: 'The Matrix Reloaded', year: 2003, runtime: 138, movieId: 9 }] }) });
    const fixture = TestBed.createComponent(AddMoviePage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', ' matrix ');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.lookupMovies).toHaveBeenCalledWith('matrix');
    expect(el.querySelectorAll('h2').length).toBe(2);
    expect(el.querySelector('a[href="/movies/9"]')?.textContent).toContain('In library');

    button(el, 'Add').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-movie-modal')).not.toBeNull();
  });

  it('says when nothing was found', async () => {
    setupTest({ api: mockApi({ lookupMovies: async () => [] }) });
    const fixture = TestBed.createComponent(AddMoviePage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', 'zzz');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(text(el)).toContain('No movies found.');
  });
});
