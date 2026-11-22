import { signal } from '@angular/core';
import { afterEach, describe, expect, it } from 'vitest';
import { Selection, storeView, storedView } from './selection';

const items = (...ids: number[]) => ids.map(id => ({ id }));

describe('Selection', () => {
  afterEach(() => localStorage.clear());

  it('toggles titles and keeps list order', () => {
    const selection = new Selection(signal(items(1, 2, 3)));
    selection.toggle(3);
    selection.toggle(1);
    expect(selection.selected()).toEqual([1, 3]);
    selection.toggle(3);
    expect(selection.selected()).toEqual([1]);
  });

  it('selects ranges with Shift', () => {
    const selection = new Selection(signal(items(1, 2, 3, 4, 5)));
    selection.toggle(2);
    selection.toggle(4, true);
    expect(selection.selected()).toEqual([2, 3, 4]);
    // A Shift+click on a selected title clears the range from the anchor.
    selection.toggle(3, true);
    expect(selection.selected()).toEqual([2]);
  });

  it('only counts titles in scope', () => {
    const scope = signal(items(1, 2, 3));
    const selection = new Selection(scope);
    selection.toggleAll();
    expect(selection.all()).toBe(true);
    scope.set(items(2, 3, 4));
    expect(selection.selected()).toEqual([2, 3]);
    expect(selection.all()).toBe(false);
    selection.toggleAll();
    expect(selection.selected()).toEqual([2, 3, 4]);
    selection.toggleAll();
    expect(selection.count()).toBe(0);
  });

  it('remembers the view mode', () => {
    expect(storedView('k')).toBe('grid');
    storeView('k', 'table');
    expect(storedView('k')).toBe('table');
  });
  it('selects by a custom key', () => {
    const selection = new Selection(signal([{ hash: 'a' }, { hash: 'b' }, { hash: 'c' }]), t => t.hash);
    selection.toggle('a');
    selection.toggle('c', true);
    expect(selection.selected()).toEqual(['a', 'b', 'c']);
    expect(selection.all()).toBe(true);
  });
});
