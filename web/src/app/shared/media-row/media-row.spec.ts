import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { DiscoverItem } from '../../core/models';
import { settle, setupTest, text } from '../../../testing/test-setup';
import { MediaRow } from './media-row';

const items: DiscoverItem[] = [
  { kind: 'movie', item: { tmdbId: 1, title: 'Arrival', year: 2016, runtime: 0 } },
  { kind: 'movie', item: { tmdbId: 2, title: 'Sicario', year: 2015, runtime: 0 } },
];

async function render(inputs: Record<string, unknown>) {
  setupTest();
  const fixture = TestBed.createComponent(MediaRow);
  fixture.componentRef.setInput('title', 'Trending');
  for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
  await settle(fixture);
  return fixture;
}

describe('MediaRow', () => {
  it('renders a card per item and the see-all link', async () => {
    const fixture = await render({ items, link: ['/discover', 'movies', 'trending'] });
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelectorAll('kr-media-card').length).toBe(2);
    expect(el.querySelector('a[href="/discover/movies/trending"]')?.textContent).toContain('See all');
  });

  it('opens the add dialog for a card', async () => {
    const fixture = await render({ items });
    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('kr-media-card button') as HTMLButtonElement).click();
    await settle(fixture);
    expect(el.querySelector('kr-discover-add')).not.toBeNull();
  });

  it('shows loading, error and empty states', async () => {
    let fixture = await render({ loading: true });
    expect(fixture.nativeElement.querySelectorAll('.animate-pulse').length).toBe(8);
    fixture.componentRef.setInput('loading', false);
    fixture.componentRef.setInput('empty', 'Nothing trending.');
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Nothing trending.');
    fixture.componentRef.setInput('error', 'TMDB is down');
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('TMDB is down');
  });
});
