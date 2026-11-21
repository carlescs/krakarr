import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { settle, text } from '../../../testing/test-setup';
import { CreditsView } from './credits';

describe('CreditsView', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));

  it('lists crew and cast with links to their pages', async () => {
    const fixture = TestBed.createComponent(CreditsView);
    fixture.componentRef.setInput('crew', [{ tmdbId: 1, name: 'Ridley Scott', jobs: 'Director' }]);
    fixture.componentRef.setInput('cast', [
      { tmdbId: 2, name: 'Sigourney Weaver', character: 'Ripley', profileUrl: 'https://example.test/sw.jpg' },
      { tmdbId: 3, name: 'John Hurt', episodes: 1 },
    ]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Ridley Scott');
    expect(text(el)).toContain('Director');
    expect(text(el)).toContain('Ripley');
    expect(text(el)).toContain('1 episode');
    // No photo: initials instead.
    expect(text(el)).toContain('JH');
    expect([...el.querySelectorAll('a')].map(a => a.getAttribute('href'))).toEqual(['/people/1', '/people/2', '/people/3']);
  });

  it('shows placeholders while loading and nothing when empty', async () => {
    const fixture = TestBed.createComponent(CreditsView);
    fixture.componentRef.setInput('loading', true);
    await settle(fixture);
    expect(fixture.nativeElement.querySelectorAll('.animate-pulse').length).toBe(8);

    fixture.componentRef.setInput('loading', false);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toBe('');
  });
});
