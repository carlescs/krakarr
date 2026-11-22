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

async function render(indexers: IndexerQueryStatus[], searching: boolean) {
  setupTest();
  const fixture = TestBed.createComponent(IndexerProgress);
  fixture.componentRef.setInput('indexers', indexers);
  fixture.componentRef.setInput('searching', searching);
  await settle(fixture);
  return fixture.nativeElement as HTMLElement;
}

describe('IndexerProgress', () => {
  it('counts finished indexers while searching and names the ones still running', async () => {
    const el = await render(statuses, true);
    expect(text(el, '[data-summary]')).toBe('Searching indexers… 2 of 3 done');
    expect(text(el, '[data-state="Done"]')).toBe('Nyaa 12');
    expect(el.querySelector('[data-state="Done"]')?.getAttribute('title')).toBe('12 result(s) in 1.3 s');
    expect(text(el)).toContain('Waiting for Slow');
    expect(text(el, '[data-error]')).toBe('Broken: Timed out after 60 seconds');
  });

  it('summarises a finished search', async () => {
    const el = await render(statuses.slice(0, 2), false);
    expect(text(el, '[data-summary]')).toBe('2 indexers queried · 1 failed');
    expect(text(el)).not.toContain('Waiting');
  });

  it('says when there was nothing to search', async () => {
    const el = await render([], false);
    expect(text(el, '[data-summary]')).toBe('No enabled indexers to search');
  });
});
