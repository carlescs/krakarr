import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { SearchIndexer } from '../../core/models';
import { button, settle, text, type } from '../../../testing/test-setup';
import { IndexerPicker } from './indexer-picker';

@Component({
  imports: [IndexerPicker],
  template: `<kr-indexer-picker [indexers]="indexers()" [(selected)]="selected" [disabled]="disabled()" />`,
})
class Host {
  indexers = signal<SearchIndexer[]>([
    { id: 1, name: 'Nyaa' },
    { id: 2, name: '1337x' },
    { id: 3, name: 'TorrentLeech' },
  ]);
  selected = signal<number[]>([]);
  disabled = signal(false);
}

describe('IndexerPicker', () => {
  it('selects one or more indexers and resets back to all', async () => {
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const trigger = el.querySelector('button')!;

    expect(text(trigger)).toBe('All indexers');
    expect(el.querySelector('[role="group"]')).toBeNull();

    trigger.click();
    await settle(fixture);
    expect(el.querySelector('[role="group"]')).not.toBeNull();

    const checkboxes = () => [...el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    expect(checkboxes().length).toBe(3);

    // Selecting one indexer shows its name on the trigger.
    checkboxes()[0].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([1]);
    expect(text(trigger)).toBe('Nyaa');

    // Selecting a second indexer shows the count and lists both in the title.
    checkboxes()[2].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([1, 3]);
    expect(text(trigger)).toBe('2 indexers');
    expect(trigger.title).toBe('Nyaa, TorrentLeech');

    // Resetting clears the selection back to all indexers.
    button(el, 'All indexers').click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([]);
    expect(text(trigger)).toBe('All indexers');

    // Escape closes the popover.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(fixture);
    expect(el.querySelector('[role="group"]')).toBeNull();
  });

  it('filters long indexer lists inside the popover', async () => {
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.indexers.set([
      { id: 1, name: 'Alpha' }, { id: 2, name: 'Beta' }, { id: 3, name: 'Gamma' }, { id: 4, name: 'Delta' },
      { id: 5, name: 'Epsilon' }, { id: 6, name: 'Zeta' }, { id: 7, name: 'Nyaa' },
    ]);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector('button')!.click();
    await settle(fixture);

    type(el, 'input[type="search"]', 'nyaa');
    await settle(fixture);
    const labels = [...el.querySelectorAll('label')].map(l => text(l));
    expect(labels).toEqual(['Nyaa']);

    // Closing outside resets the filter.
    document.body.click();
    await settle(fixture);
    expect(el.querySelector('[role="group"]')).toBeNull();
  });
});
