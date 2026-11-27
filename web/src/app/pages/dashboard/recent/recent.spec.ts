import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { DashboardRecent } from '../../../core/models';
import { settle, setupTest, text } from '../../../../testing/test-setup';
import { PAGE_SIZE, Recent, SWIPE_THRESHOLD } from './recent';

function item(id: number, overrides: Partial<DashboardRecent> = {}): DashboardRecent {
  return { mediaType: 'Movie', id, title: `Movie ${id}`, year: 2020, date: '2026-01-01T00:00:00Z', ...overrides };
}

const many = (count: number) => Array.from({ length: count }, (_, i) => item(i + 1));

async function render(imported: DashboardRecent[], added: DashboardRecent[] = []) {
  setupTest();
  const fixture = TestBed.createComponent(Recent);
  fixture.componentRef.setInput('imported', imported);
  fixture.componentRef.setInput('added', added);
  await settle(fixture);
  return { fixture, el: fixture.nativeElement as HTMLElement };
}

/** The page the track shows: the only one not hidden from assistive technology. */
const shown = (el: HTMLElement) => el.querySelector('[data-page][aria-hidden="false"]')?.getAttribute('data-page');

function pointer(target: Element, type: string, x: number, y = 0) {
  const event = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 });
  Object.defineProperty(event, 'pointerId', { value: 1 });
  Object.defineProperty(event, 'pointerType', { value: 'touch' });
  target.dispatchEvent(event);
}

async function swipe(fixture: { detectChanges(): void }, el: HTMLElement, dx: number, dy = 0) {
  const track = el.querySelector('[aria-roledescription="carousel"]')!;
  pointer(track, 'pointerdown', 200, 100);
  pointer(track, 'pointermove', 200 + dx / 2, 100 + dy / 2);
  pointer(track, 'pointermove', 200 + dx, 100 + dy);
  pointer(track, 'pointerup', 200 + dx, 100 + dy);
  await settle(fixture as never);
}

describe('Recent', () => {
  it('splits the list into pages with a dot for each', async () => {
    const { fixture, el } = await render(many(PAGE_SIZE * 2 + 2));

    expect(el.querySelectorAll('[data-page]').length).toBe(3);
    expect(el.querySelectorAll('[data-page="0"] li').length).toBe(PAGE_SIZE);
    expect(el.querySelectorAll('[data-page="2"] li').length).toBe(2);
    const dots = [...el.querySelectorAll('[data-pager] button')] as HTMLButtonElement[];
    expect(dots.map(d => d.getAttribute('aria-label'))).toEqual(['Page 1', 'Page 2', 'Page 3']);
    expect(shown(el)).toBe('0');
    expect(el.querySelector('[data-page="1"]')!.hasAttribute('inert')).toBe(true);

    dots[2].click();
    await settle(fixture);
    expect(shown(el)).toBe('2');
    expect(dots[2].getAttribute('aria-current')).toBe('true');
    expect(dots[0].hasAttribute('aria-current')).toBe(false);
  });

  it('turns the page with a horizontal swipe', async () => {
    const { fixture, el } = await render(many(PAGE_SIZE * 3));

    await swipe(fixture, el, -(SWIPE_THRESHOLD + 10));
    expect(shown(el)).toBe('1');
    await swipe(fixture, el, -(SWIPE_THRESHOLD + 10));
    await swipe(fixture, el, -(SWIPE_THRESHOLD + 10)); // already on the last page
    expect(shown(el)).toBe('2');
    await swipe(fixture, el, SWIPE_THRESHOLD + 10);
    expect(shown(el)).toBe('1');

    // Short drags snap back, and mostly vertical ones are left to the page's scrolling.
    await swipe(fixture, el, -(SWIPE_THRESHOLD - 10));
    expect(shown(el)).toBe('1');
    await swipe(fixture, el, -(SWIPE_THRESHOLD + 10), 200);
    expect(shown(el)).toBe('1');
  });

  it('does not open a poster at the end of a swipe', async () => {
    const { el } = await render(many(PAGE_SIZE * 2));
    const track = el.querySelector('[aria-roledescription="carousel"]')!;
    const link = el.querySelector('[data-page="0"] a')!;
    pointer(link, 'pointerdown', 200);
    pointer(link, 'pointermove', 150);
    pointer(link, 'pointerup', 140);

    // Browsers send the click right after the pointerup that ends the drag.
    const afterSwipe = new MouseEvent('click', { bubbles: true, cancelable: true });
    link.dispatchEvent(afterSwipe);
    expect(afterSwipe.defaultPrevented).toBe(true);

    // Only that one click: the next goes through.
    const next = new MouseEvent('click', { bubbles: true, cancelable: true });
    track.dispatchEvent(next);
    expect(next.defaultPrevented).toBe(false);
  });

  it('reacts to the next tap after a swipe that ends without a click', async () => {
    const { fixture, el } = await render(many(PAGE_SIZE * 3));
    await swipe(fixture, el, -(SWIPE_THRESHOLD + 10));
    (el.querySelectorAll('[data-pager] button')[2] as HTMLButtonElement).click();
    await settle(fixture);
    expect(shown(el)).toBe('2');

    await swipe(fixture, el, SWIPE_THRESHOLD + 10);
    await new Promise(resolve => setTimeout(resolve));
    const tap = new MouseEvent('click', { bubbles: true, cancelable: true });
    el.querySelector('[aria-roledescription="carousel"]')!.dispatchEvent(tap);
    expect(tap.defaultPrevented).toBe(false);
  });

  it('switches tabs and starts the new list on its first page', async () => {
    const { fixture, el } = await render(many(PAGE_SIZE * 2), [item(8, { mediaType: 'Series', title: 'Andor' })]);
    (el.querySelectorAll('[data-pager] button')[1] as HTMLButtonElement).click();
    await settle(fixture);
    expect(shown(el)).toBe('1');

    ([...el.querySelectorAll('[role="tab"]')] as HTMLElement[]).find(t => t.textContent!.trim() === 'Added')!.click();
    await settle(fixture);
    expect(el.querySelector('[role="tab"][aria-selected="true"]')!.textContent).toContain('Added');
    expect(shown(el)).toBe('0');
    expect(el.querySelector('[data-list="recently-added"] a[href="/series/8"]')).not.toBeNull();
    expect(text(el, '[data-list="recently-added"]')).toContain('Series');
    // A single page needs no dots.
    expect(el.querySelector('[data-pager]')).toBeNull();
  });

  it('says when either list is empty', async () => {
    const { fixture, el } = await render([], []);
    expect(text(el)).toContain('Nothing imported yet.');
    ([...el.querySelectorAll('[role="tab"]')] as HTMLElement[]).find(t => t.textContent!.trim() === 'Added')!.click();
    await settle(fixture);
    expect(text(el)).toContain('Nothing in the library yet.');
  });
});
