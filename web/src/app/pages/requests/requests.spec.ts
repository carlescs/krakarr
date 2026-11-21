import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MediaRequest, UserRole } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { RequestsPage } from './requests';

const request = (r: Partial<MediaRequest>): MediaRequest => ({
  id: 1, mediaType: 'Movie', tmdbId: 1, title: 'Arrival', year: 2016, status: 'Pending', requestedById: 2, requestedBy: 'kim', requestedAt: '2026-01-01T00:00:00Z', ...r,
});

async function render(role: UserRole, results: MediaRequest[], extra: Record<string, (...args: never[]) => unknown> = {}) {
  const api = setupTest({ role, api: mockApi({ requests: async () => ({ results, page: 1, totalPages: 1, total: results.length }), ...extra }) });
  const fixture = TestBed.createComponent(RequestsPage);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('RequestsPage', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('shows staff the pending queue with approve and decline', async () => {
    const { api, fixture, el } = await render('Manager', [request({})], { declineRequest: async () => undefined });
    expect(api.requests).toHaveBeenCalledWith('Pending', 1);
    expect(text(el, 'h1')).toBe('Requests');
    expect(text(el)).toContain('Requested by kim');

    button(el, 'Approve').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-movie-modal')).not.toBeNull();

    button(el, 'Cancel').click();
    await settle(fixture);
    button(el, 'Decline').click();
    await settle(fixture);
    type(el, '#decline-reason', ' Not on any service ');
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Decline').click();
    await settle(fixture);
    expect(api.declineRequest).toHaveBeenCalledWith(1, 'Not on any service');
  });

  it('switches filters', async () => {
    const { api, fixture, el } = await render('Admin', []);
    expect(text(el)).toContain('No pending requests.');
    button(el, 'All').click();
    await settle(fixture);
    expect(api.requests).toHaveBeenLastCalledWith(undefined, 1);
    expect(text(el)).toContain('No requests.');
  });

  it('marks the results of the user\'s requests as seen', async () => {
    const { api } = await render('User', [request({ status: 'Approved' })], { markRequestsSeen: async () => undefined });
    expect(api.markRequestsSeen).toHaveBeenCalled();
  });

  it('lets users withdraw their own pending requests', async () => {
    vi.stubGlobal('confirm', () => true);
    const { api, fixture, el } = await render('User', [request({ id: 5 })], { deleteRequest: async () => undefined });
    expect(api.requests).toHaveBeenCalledWith(undefined, 1);
    expect(text(el, 'h1')).toBe('My requests');
    expect(text(el, 'li')).not.toContain('Approve');
    button(el, 'Withdraw').click();
    await settle(fixture);
    expect(api.deleteRequest).toHaveBeenCalledWith(5);
  });

  it('points users with no requests to Discover', async () => {
    const { el } = await render('User', []);
    expect(text(el)).toContain("You haven't requested anything yet.");
    expect(el.querySelector('a[href="/discover"]')).not.toBeNull();
  });
});
