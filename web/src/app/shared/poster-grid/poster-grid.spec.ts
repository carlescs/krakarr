import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { PosterGridSkeleton } from './poster-grid';

describe('PosterGridSkeleton', () => {
  it('renders the requested number of placeholder cards', async () => {
    const fixture = TestBed.createComponent(PosterGridSkeleton);
    fixture.componentRef.setInput('count', 5);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelectorAll('.animate-pulse').length).toBe(5);
    expect(fixture.nativeElement.querySelector('[aria-busy="true"]')).not.toBeNull();
  });
});
