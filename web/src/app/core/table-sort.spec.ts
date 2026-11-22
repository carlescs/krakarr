import { signal } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { TableSort } from './table-sort';

interface Row { name: string; size?: number | null }
const rows: Row[] = [
  { name: 'Episode 10', size: 5 },
  { name: 'episode 2', size: null },
  { name: 'Alpha', size: 9 },
  { name: 'Beta', size: 5 },
];

const names = (list: readonly Row[]) => list.map(r => r.name);

describe('TableSort', () => {
  it('keeps the source order until a column is chosen', () => {
    const sort = new TableSort(signal(rows), { name: r => r.name });
    expect(sort.rows()).toBe(rows);
    expect(sort.ariaSort('name')).toBe('none');
  });

  it('sorts text naturally and case-insensitively, then reverses, then resets', () => {
    const sort = new TableSort(signal(rows), { name: r => r.name });
    sort.toggle('name');
    expect(names(sort.rows())).toEqual(['Alpha', 'Beta', 'episode 2', 'Episode 10']);
    expect(sort.ariaSort('name')).toBe('ascending');
    sort.toggle('name');
    expect(names(sort.rows())).toEqual(['Episode 10', 'episode 2', 'Beta', 'Alpha']);
    expect(sort.ariaSort('name')).toBe('descending');
    sort.toggle('name');
    expect(sort.rows()).toBe(rows);
  });

  it('starts descending when the column asks for it, keeps unknown values last and ties in source order', () => {
    const sort = new TableSort(signal(rows), { size: { value: r => r.size, desc: true } });
    sort.toggle('size');
    expect(names(sort.rows())).toEqual(['Alpha', 'Episode 10', 'Beta', 'episode 2']);
    sort.toggle('size');
    expect(names(sort.rows())).toEqual(['Episode 10', 'Beta', 'Alpha', 'episode 2']);
  });

  it('returns to its initial column and follows source changes', () => {
    const source = signal(rows);
    const sort = new TableSort(source, { name: 'source', size: r => r.size }, { key: 'name' });
    expect(sort.ariaSort('name')).toBe('ascending');
    sort.toggle('name');
    expect(names(sort.rows())).toEqual(['Beta', 'Alpha', 'episode 2', 'Episode 10']);
    sort.toggle('name');
    expect(sort.rows()).toBe(rows);
    sort.toggle('size');
    sort.toggle('size');
    sort.toggle('size');
    expect(sort.state()).toEqual({ key: 'name', desc: false });

    sort.toggle('size');
    source.set([{ name: 'Zed', size: 1 }, ...rows]);
    expect(names(sort.rows())[0]).toBe('Zed');
  });
});
