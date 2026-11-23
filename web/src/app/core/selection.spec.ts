import { signal } from '@angular/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PagedSelection, Selection, storeView, storedView } from './selection';

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

describe('PagedSelection', () => {
  it('selects local ranges synchronously and falls back to fetchAllIds across unloaded gaps', async () => {
    const total = signal(5);
    const loaded = [1, 2, 5];
    const fetchAllIds = vi.fn(async () => [1, 2, 3, 4, 5]);
    const selection = new PagedSelection(
      total,
      () => loaded,
      () => false,
      (a, b) => (a <= 2 && b <= 2 ? [1, 2] : null),
      fetchAllIds,
    );

    selection.toggle(1);
    selection.toggle(2, true);
    expect(selection.selected()).toEqual([1, 2]);
    expect(fetchAllIds).not.toHaveBeenCalled();

    await selection.toggle(5, true);
    expect(fetchAllIds).toHaveBeenCalledTimes(1);
    expect(selection.selected().sort((a, b) => a - b)).toEqual([1, 2, 3, 4, 5]);
    expect(selection.all()).toBe(true);
  });

  it('uses loadedIds when allLoaded is true and fetches all ids when paginated', async () => {
    const total = signal(3);
    let loadedAll = true;
    let serverIds = [10, 20, 30];
    const fetchAllIds = vi.fn(async () => serverIds);
    const selection = new PagedSelection(total, () => [10, 20, 30], () => loadedAll, () => null, fetchAllIds);

    await selection.toggleAll();
    expect(selection.selected()).toEqual([10, 20, 30]);
    expect(fetchAllIds).not.toHaveBeenCalled();

    await selection.toggleAll();
    expect(selection.count()).toBe(0);

    loadedAll = false;
    total.set(4);
    serverIds = [10, 20, 30, 40];
    await selection.toggleAll();
    expect(fetchAllIds).toHaveBeenCalledTimes(1);
    expect(selection.count()).toBe(4);

    // Narrow filter and reconcile against server ids
    total.set(2);
    serverIds = [20, 40];
    await selection.reconcile();
    expect(selection.selected().sort((a, b) => a - b)).toEqual([20, 40]);
  });
});
