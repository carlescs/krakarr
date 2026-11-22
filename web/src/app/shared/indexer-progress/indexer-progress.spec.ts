import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { IndexerQueryStatus } from '../../core/models';
import { settle, setupTest, text } from '../../../testing/test-setup';
import { IndexerProgress } from './indexer-progress';

const statuses: IndexerQueryStatus[] = [
  { indexerId: 1, indexerName: 'Nyaa', state: 'Done', releases: 12, seconds: 1.25 },
  { indexerId: 2, indexerName: 'Broken', state: 'Failed', message: 'Timed out after 60 seconds', seconds: 60 },
  { indexerId: 3, indexerName: 'Slow', state: 'Pending' },
];

async function render(indexers: IndexerQueryStatus[], searching: boolean, selected: ReadonlySet<number> | null = null) {
  setupTest();
  const fixture = TestBed.createComponent(IndexerProgress);
  fixture.componentRef.setInput('indexers', indexers);
  fixture.componentRef.setInput('searching', searching);
  fixture.componentRef.setInput('selected', selected);
  await settle(fixture);
  return { el: fixture.nativeElement as HTMLElement, component: fixture.componentInstance };
}

describe('IndexerProgress', () => {
  it('counts finished indexers while searching and names the ones still running', async () => {
    const { el } = await render(statuses, true);
    expect(text(el, '[data-summary]')).toBe('Searching indexers… 2 of 3 done');
    expect(text(el, '[data-state="Done"]')).toBe('Nyaa 12');
    expect(el.querySelector('[data-state="Done"]')?.getAttribute('title')).toBe('12 result(s) in 1.3 s');
    expect(text(el)).toContain('Waiting for Slow');
    expect(text(el, '[data-error]')).toBe('Broken: Timed out after 60 seconds');
  });

  it('summarises a finished search', async () => {
    const { el } = await render(statuses.slice(0, 2), false);
    expect(text(el, '[data-summary]')).toBe('2 indexers queried · 1 failed');
    expect(text(el)).not.toContain('Waiting');
  });

  it('says when there was nothing to search', async () => {
    const { el } = await render([], false);
    expect(text(el, '[data-summary]')).toBe('No enabled indexers to search');
  });

  const withResults: IndexerQueryStatus[] = [
    { indexerId: 1, indexerName: 'Nyaa', state: 'Done', releases: 12 },
    { indexerId: 4, indexerName: 'Other', state: 'Done', releases: 3 },
    { indexerId: 5, indexerName: 'Empty', state: 'Done', releases: 0 },
    { indexerId: 2, indexerName: 'Broken', state: 'Failed', message: 'x' },
  ];

  it('lets the chips of indexers with results filter', async () => {
    const { el, component } = await render(withResults, false, new Set([4]));
    const toggled: number[] = [];
    let cleared = 0;
    component.toggled.subscribe(id => toggled.push(id));
    component.cleared.subscribe(() => cleared++);

    const buttons = [...el.querySelectorAll<HTMLButtonElement>('button[data-state]')];
    expect(buttons.map(b => b.textContent?.trim())).toEqual(['Nyaa 12', 'Other 3']);
    expect(buttons.map(b => b.getAttribute('aria-pressed'))).toEqual(['false', 'true']);
    buttons[0].click();
    expect(toggled).toEqual([1]);

    el.querySelector<HTMLButtonElement>('[data-clear-filter]')!.click();
    expect(cleared).toBe(1);
  });

  it('lets the chips filter while results still stream in', async () => {
    const { el } = await render([...withResults, { indexerId: 3, indexerName: 'Slow', state: 'Pending' }], true, new Set());
    expect([...el.querySelectorAll('button[data-state]')].map(b => b.textContent?.trim())).toEqual(['Nyaa 12', 'Other 3']);
  });

  it('keeps the chips plain without a filter', async () => {
    expect((await render(withResults, false)).el.querySelector('button')).toBeNull();
  });

  it('keeps the chips plain when a single indexer returned results', async () => {
    expect((await render(withResults.slice(1), false, new Set())).el.querySelector('button')).toBeNull();
  });
});
