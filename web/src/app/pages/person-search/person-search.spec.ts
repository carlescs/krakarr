import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { PersonSearchPage } from './person-search';

describe('PersonSearchPage', () => {
  it('searches TMDB and links each person to their filmography page', async () => {
    const api = setupTest({
      api: mockApi({
        lookupPeople: async () => [
          { tmdbId: 6384, name: 'Keanu Reeves', knownForDepartment: 'Acting', profileUrl: 'https://img/k.jpg', knownFor: ['The Matrix', 'John Wick'] },
          { tmdbId: 525, name: 'Christopher Nolan', knownForDepartment: 'Directing', knownFor: ['Inception'] },
        ],
      }),
    });
    const fixture = TestBed.createComponent(PersonSearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', ' keanu ');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.lookupPeople).toHaveBeenCalledWith('keanu');
    expect(el.querySelectorAll('h2').length).toBe(2);
    expect(el.querySelector('a[href="/people/6384"]')?.textContent).toContain('The Matrix, John Wick');
    expect(el.querySelector('a[href="/people/525"]')?.textContent).toContain('CN');
  });

  it('says when no one was found', async () => {
    setupTest({ api: mockApi({ lookupPeople: async () => [] }) });
    const fixture = TestBed.createComponent(PersonSearchPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', 'zzz');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(text(el)).toContain('No people found.');
  });
});
