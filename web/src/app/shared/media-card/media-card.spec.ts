import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { DiscoverItem, UserRole } from '../../core/models';
import { settle, setupTest, text } from '../../../testing/test-setup';
import { MediaCard, detailRoute, discoverKey, libraryRoute } from './media-card';

async function render(item: DiscoverItem, role: UserRole = 'Admin') {
  setupTest({ role });
  const fixture = TestBed.createComponent(MediaCard);
  fixture.componentRef.setInput('item', item);
  await settle(fixture);
  return fixture;
}

const arrival: DiscoverItem = { kind: 'movie', item: { tmdbId: 329865, title: 'Arrival', year: 2016, runtime: 116, rating: 7.6 } };

describe('MediaCard', () => {
  it('links to the TMDB page and offers to add titles outside the library', async () => {
    const fixture = await render(arrival);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('a')?.getAttribute('href')).toBe('/movies/tmdb/329865');
    expect(text(el)).toContain('★ 7.6');
    const add = vi.fn();
    fixture.componentInstance.add.subscribe(add);
    (el.querySelector('button') as HTMLButtonElement).click();
    expect(add).toHaveBeenCalledWith(arrival);
  });

  it('offers plain users a request instead', async () => {
    const fixture = await render(arrival, 'User');
    expect(fixture.nativeElement.querySelector('button')?.textContent?.trim()).toBe('Request');
  });

  it('links library titles to their page', async () => {
    const fixture = await render({ kind: 'series', item: { tmdbId: 1, title: 'Dark', year: 2017, seriesId: 8 } });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('a')?.getAttribute('href')).toBe('/series/8');
    expect(el.querySelector('[title="In library"]')).not.toBeNull();
    expect(el.querySelector('button')).toBeNull();
  });
});

describe('media card routes', () => {
  it('builds keys and routes', () => {
    expect(discoverKey(arrival)).toBe('movie-329865');
    expect(discoverKey({ kind: 'series', item: { tvdbId: 5, title: 'X', year: 0 } })).toBe('series-tvdb5');
    expect(libraryRoute(arrival)).toBeNull();
    expect(detailRoute(arrival)).toEqual(['/movies', 'tmdb', 329865]);
    expect(detailRoute({ kind: 'series', item: { tvdbId: 5, title: 'X', year: 0 } })).toBeNull();
  });
});
