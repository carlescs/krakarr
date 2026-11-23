import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, settle, text, type } from '../../../testing/test-setup';
import { MultiPicker, PickerOption } from './multi-picker';

@Component({
  imports: [MultiPicker],
  template: `<kr-multi-picker [options]="options()" [(selected)]="selected" [disabled]="disabled()" plural="indexers" />`,
})
class NumericHost {
  options = signal<PickerOption<number>[]>([
    { id: 1, name: 'Nyaa' },
    { id: 2, name: '1337x' },
    { id: 3, name: 'TorrentLeech' },
  ]);
  selected = signal<number[]>([]);
  disabled = signal(false);
}

@Component({
  imports: [MultiPicker],
  template: `<kr-multi-picker [options]="options()" [(selected)]="selected" plural="providers" qualifier="configured" />`,
})
class StringHost {
  options = signal<PickerOption<string>[]>([
    { id: 'opensubtitles', name: 'OpenSubtitles' },
    { id: 'subdl', name: 'SubDL' },
  ]);
  selected = signal<string[]>([]);
}

describe('MultiPicker', () => {
  it('selects one or more options and resets back to all', async () => {
    const fixture = TestBed.createComponent(NumericHost);
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

    // Selecting one option shows its name on the trigger.
    checkboxes()[0].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([1]);
    expect(text(trigger)).toBe('Nyaa');

    // Selecting a second option shows the count and lists both in the title.
    checkboxes()[2].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([1, 3]);
    expect(text(trigger)).toBe('2 indexers');
    expect(trigger.title).toBe('Nyaa, TorrentLeech');

    // Resetting clears the selection back to all options.
    button(el, 'All indexers').click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual([]);
    expect(text(trigger)).toBe('All indexers');

    // Escape closes the popover.
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle(fixture);
    expect(el.querySelector('[role="group"]')).toBeNull();
  });

  it('supports string ids and custom labels', async () => {
    const fixture = TestBed.createComponent(StringHost);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const trigger = el.querySelector('button')!;

    expect(text(trigger)).toBe('All providers');
    expect(trigger.title).toBe('All configured providers');

    trigger.click();
    await settle(fixture);
    expect(text(el, '[role="group"]')).toContain('All configured (2)');

    const checkboxes = () => [...el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')];
    checkboxes()[1].click();
    await settle(fixture);
    expect(fixture.componentInstance.selected()).toEqual(['subdl']);
    expect(text(trigger)).toBe('SubDL');
  });

  it('filters long option lists inside the popover', async () => {
    const fixture = TestBed.createComponent(NumericHost);
    fixture.componentInstance.options.set([
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
