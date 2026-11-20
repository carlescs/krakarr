import { describe, expect, it } from 'vitest';
import { MediaRequest } from './models';
import { canDeleteRequest, requestBadge, requestRoute } from './requests';

const base: MediaRequest = {
  id: 1, mediaType: 'Movie', tmdbId: 603, title: 'The Matrix', year: 1999, status: 'Pending', requestedById: 2, requestedAt: '2026-11-20T10:00:00Z',
};

describe('requests', () => {
  it('labels requests by status and availability', () => {
    expect(requestBadge(base).label).toBe('Pending');
    expect(requestBadge({ ...base, status: 'Declined' }).label).toBe('Declined');
    expect(requestBadge({ ...base, status: 'Approved', availability: 'Available', libraryId: 5 }).label).toBe('Available');
    expect(requestBadge({ ...base, status: 'Approved', availability: 'Partial', libraryId: 5 }).label).toBe('Partially available');
    expect(requestBadge({ ...base, status: 'Approved', availability: 'Missing', libraryId: 5 }).label).toContain('downloading');
    expect(requestBadge({ ...base, status: 'Approved' }).label).toContain('removed');
  });

  it('opens the library page once added, the TMDB page before', () => {
    expect(requestRoute(base)).toEqual(['/movies', 'tmdb', 603]);
    expect(requestRoute({ ...base, mediaType: 'Series', libraryId: 9 })).toEqual(['/series', 9]);
  });

  it('lets users withdraw only pending requests', () => {
    expect(canDeleteRequest(base, false)).toBe(true);
    expect(canDeleteRequest({ ...base, status: 'Declined' }, false)).toBe(false);
    expect(canDeleteRequest({ ...base, status: 'Approved' }, true)).toBe(true);
  });
});
