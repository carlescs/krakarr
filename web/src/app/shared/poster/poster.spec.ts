import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Poster } from './poster';

describe('Poster', () => {
  it('shows the image when there is a url', async () => {
    const fixture = TestBed.createComponent(Poster);
    fixture.componentRef.setInput('url', 'https://example.test/p.jpg');
    fixture.componentRef.setInput('title', 'Alien');
    await fixture.whenStable();
    const img = fixture.nativeElement.querySelector('img') as HTMLImageElement;
    expect(img.src).toBe('https://example.test/p.jpg');
    expect(img.alt).toBe('Alien');
  });

  it('falls back to the title', async () => {
    const fixture = TestBed.createComponent(Poster);
    fixture.componentRef.setInput('title', 'Alien');
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('img')).toBeNull();
    expect(fixture.nativeElement.textContent.trim()).toBe('Alien');
  });
});
