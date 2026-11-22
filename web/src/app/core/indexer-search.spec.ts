import { describe, expect, it } from 'vitest';
import { mergeStatuses } from './indexer-search';

describe('mergeStatuses', () => {
  it('takes newer states but never moves a finished indexer back to pending', () => {
    const merged = mergeStatuses(
      [{ indexerId: 1, indexerName: 'A', state: 'Done', releases: 3 }, { indexerId: 2, indexerName: 'B', state: 'Pending' }],
      [{ indexerId: 1, indexerName: 'A', state: 'Pending' }, { indexerId: 2, indexerName: 'B', state: 'Failed', message: 'x' }, { indexerId: 3, indexerName: 'C', state: 'Pending' }],
    );
    expect(merged.map(s => s.state)).toEqual(['Done', 'Failed', 'Pending']);
    expect(merged[0].releases).toBe(3);
  });
});
