import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { TableSort } from '../../core/table-sort';
import { button, settle, setupTest, text } from '../../../testing/test-setup';
import { SortHeader } from './sort-header';

@Component({
  imports: [SortHeader],
  template: `
    <table>
      <thead><tr><th krSort="name" [krSortBy]="sort" [krSortDisabled]="disabled()">Name</th></tr></thead>
      <tbody>@for (r of sort.rows(); track r) { <tr><td>{{ r }}</td></tr> }</tbody>
    </table>`,
})
class Host {
  readonly disabled = signal(false);
  readonly sort = new TableSort(signal(['b', 'c', 'a']), { name: (r: string) => r });
}

const cells = (el: HTMLElement) => [...el.querySelectorAll('td')].map(td => td.textContent).join(' ');

describe('SortHeader', () => {
  it('sorts the table when the header is clicked', async () => {
    setupTest();
    const fixture = TestBed.createComponent(Host);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    const th = el.querySelector('th')!;
    expect(th.getAttribute('aria-sort')).toBe('none');
    expect(cells(el)).toBe('b c a');

    button(el, 'Name').click();
    await settle(fixture);
    expect(th.getAttribute('aria-sort')).toBe('ascending');
    expect(cells(el)).toBe('a b c');
    expect(text(el, 'th span')).toBe('↑');

    button(el, 'Name').click();
    await settle(fixture);
    expect(th.getAttribute('aria-sort')).toBe('descending');
    expect(cells(el)).toBe('c b a');
  });

  it('shows a plain label when disabled', async () => {
    setupTest();
    const fixture = TestBed.createComponent(Host);
    fixture.componentInstance.disabled.set(true);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('th button')).toBeNull();
    expect(el.querySelector('th')!.hasAttribute('aria-sort')).toBe(false);
    expect(text(el, 'th')).toBe('Name');
  });
});
