import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Overview } from './overview';

/** jsdom has no layout, so fake the heights the clamp would produce. */
function fakeHeights(scroll: number, client: number): () => void {
  const proto = Element.prototype;
  const s = Object.getOwnPropertyDescriptor(proto, 'scrollHeight')!;
  const c = Object.getOwnPropertyDescriptor(proto, 'clientHeight')!;
  Object.defineProperty(proto, 'scrollHeight', { configurable: true, get: () => scroll });
  Object.defineProperty(proto, 'clientHeight', { configurable: true, get: () => client });
  return () => {
    Object.defineProperty(proto, 'scrollHeight', s);
    Object.defineProperty(proto, 'clientHeight', c);
  };
}

async function render(text: string) {
  const fixture = TestBed.createComponent(Overview);
  fixture.componentRef.setInput('text', text);
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  return fixture;
}

describe('Overview', () => {
  it('shows short text without a toggle', async () => {
    const restore = fakeHeights(40, 40);
    try {
      const fixture = await render('A short plot.');
      const el = fixture.nativeElement as HTMLElement;
      expect(el.querySelector('[data-overview-text]')!.textContent).toContain('A short plot.');
      expect(el.querySelector('[data-overview-toggle]')).toBeNull();
    } finally { restore(); }
  });

  it('lets long text expand and collapse', async () => {
    const restore = fakeHeights(200, 80);
    try {
      const fixture = await render('A very long plot. '.repeat(50));
      const el = fixture.nativeElement as HTMLElement;
      const text = el.querySelector('[data-overview-text]')!;
      const toggle = () => el.querySelector('[data-overview-toggle]') as HTMLButtonElement;
      expect(text.classList).toContain('line-clamp-4');
      expect(toggle().textContent!.trim()).toBe('Show more');

      toggle().click();
      await fixture.whenStable();
      expect(text.classList).not.toContain('line-clamp-4');
      expect(toggle().textContent!.trim()).toBe('Show less');
      expect(toggle().getAttribute('aria-expanded')).toBe('true');

      toggle().click();
      await fixture.whenStable();
      expect(text.classList).toContain('line-clamp-4');
      expect(toggle().textContent!.trim()).toBe('Show more');
    } finally { restore(); }
  });
});
