import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { settle, setupTest } from '../../../testing/test-setup';
import { OmdbFacts } from './omdb-facts';

describe('OmdbFacts', () => {
  it('lists credits, awards and box office', async () => {
    setupTest();
    const fixture = TestBed.createComponent(OmdbFacts);
    fixture.componentRef.setInput('info', {
      imdb: 8.7, imdbVotes: 1, rottenTomatoes: 83, metacritic: 73, rottenTomatoesUrl: null,
      director: 'Lana Wachowski, Lilly Wachowski', writer: 'Lilly Wachowski', awards: 'Won 4 Oscars', boxOffice: '$172,076,928',
    });
    await settle(fixture);

    const terms = [...fixture.nativeElement.querySelectorAll('dt')].map((e: Element) => e.textContent!.trim());
    const values = [...fixture.nativeElement.querySelectorAll('dd')].map((e: Element) => e.textContent!.trim());
    expect(terms).toEqual(['Directors', 'Writer', 'Awards', 'Box office']);
    expect(values).toEqual(['Lana Wachowski, Lilly Wachowski', 'Lilly Wachowski', 'Won 4 Oscars', '$172,076,928']);
  });

  it('renders nothing without facts', async () => {
    setupTest();
    const fixture = TestBed.createComponent(OmdbFacts);
    fixture.componentRef.setInput('info', { imdb: 7, imdbVotes: 1, rottenTomatoes: null, metacritic: null, rottenTomatoesUrl: null });
    await settle(fixture);
    expect(fixture.nativeElement.querySelector('dl')).toBeNull();
  });
});
