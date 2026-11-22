import { Component, computed, input } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { TableSort } from '../../core/table-sort';

/**
 * A sortable table header: `<th krSort="size" [krSortBy]="sort">Size</th>`. The label becomes a button that sorts
 * the table by that column, with an arrow for the current direction and `aria-sort` on the header cell.
 * With `krSortDisabled` it shows the plain label (e.g. while the rows are grouped).
 */
@Component({
  selector: 'th[krSort]',
  imports: [NgTemplateOutlet],
  host: { '[attr.aria-sort]': 'disabled() ? null : sort().ariaSort(key())' },
  templateUrl: './sort-header.html',
  styleUrl: './sort-header.scss',
})
export class SortHeader {
  readonly key = input.required<string>({ alias: 'krSort' });
  readonly sort = input.required<TableSort<any, any>>({ alias: 'krSortBy' });
  readonly disabled = input(false, { alias: 'krSortDisabled' });

  protected readonly direction = computed(() => {
    if (this.disabled()) return null;
    const s = this.sort().state();
    return s.key !== this.key() ? null : s.desc ? '↓' : '↑';
  });
}
