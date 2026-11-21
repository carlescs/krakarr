import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, settle, text } from '../../../testing/test-setup';
import { CategoryPicker } from './category-picker';

const categories = [
  { id: 2000, name: 'Movies' },
  { id: 2040, name: 'Movies/HD', parentId: 2000 },
  { id: 5000, name: 'TV' },
  { id: 5040, name: 'TV/HD', parentId: 5000 },
];

async function render(selected: number[]) {
  const fixture = TestBed.createComponent(CategoryPicker);
  fixture.componentRef.setInput('categories', categories);
  fixture.componentRef.setInput('range', [2000, 2999]);
  fixture.componentRef.setInput('selected', selected);
  await settle(fixture);
  return fixture;
}

describe('CategoryPicker', () => {
  it('shows the suggested range first and the rest behind "Show all"', async () => {
    const fixture = await render([]);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('Movies');
    expect(text(el)).toContain('HD');
    expect(text(el)).not.toContain('TV');

    button(el, 'Show all categories (+1)').click();
    await settle(fixture);
    expect(text(el)).toContain('TV');
  });

  it('toggles categories in sorted order and lists unknown ones', async () => {
    const fixture = await render([9999]);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('9999');
    const boxes = el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]');
    boxes[1].click();
    boxes[0].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([2000, 2040, 9999]);

    button(el, 'Remove').click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([2000, 2040]);
  });
});
