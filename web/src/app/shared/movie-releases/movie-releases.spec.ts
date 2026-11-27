import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { MovieRelease } from '../../core/models';
import { text } from '../../../testing/test-setup';
import { MovieReleases } from './movie-releases';

describe('MovieReleases', () => {
  it('renders region, earliest worldwide, and unknown release dates', () => {
    const releases: MovieRelease[] = [
      { type: 'Theatrical', region: { date: '2026-06-23', country: 'ES' }, earliest: { date: '2026-06-10', country: 'US' } },
      { type: 'Digital', region: null, earliest: { date: '2026-08-01', country: 'US' } },
      { type: 'Physical', region: null, earliest: null },
    ];
    const fixture = TestBed.createComponent(MovieReleases);
    fixture.componentRef.setInput('releases', releases);
    fixture.componentRef.setInput('releaseRegion', 'ES');
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('In cinemas');
    expect(text(el)).toContain('ES');
    expect(text(el)).toContain('Earliest:');
    expect(text(el)).toContain('(US)');
    expect(text(el)).toContain('Digital');
    expect(text(el)).toContain('Earliest worldwide · no date for ES');
    expect(text(el)).toContain('Physical');
    expect(text(el)).toContain('Unknown');
  });
});
