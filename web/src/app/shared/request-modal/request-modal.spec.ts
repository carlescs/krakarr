import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { RequestModal } from './request-modal';

describe('RequestModal', () => {
  it('requests the title and emits the request', async () => {
    const api = setupTest({ role: 'User', api: mockApi({ createRequest: async () => ({ id: 1, title: 'Dune' }) }) });
    const fixture = TestBed.createComponent(RequestModal);
    fixture.componentRef.setInput('item', { kind: 'series', item: { tmdbId: 42, title: 'Dune', year: 2025 } });
    const requested = vi.fn();
    fixture.componentInstance.requested.subscribe(requested);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Series (all seasons)');

    button(fixture.nativeElement, 'Request').click();
    await settle(fixture);
    expect(api.createRequest).toHaveBeenCalledWith('Series', 42);
    expect(requested).toHaveBeenCalledWith({ id: 1, title: 'Dune' });
  });

  it('cannot request a title without a TMDB id', async () => {
    setupTest({ role: 'User' });
    const fixture = TestBed.createComponent(RequestModal);
    fixture.componentRef.setInput('item', { kind: 'series', item: { tvdbId: 1, title: 'Dune', year: 2025 } });
    await settle(fixture);
    expect([...fixture.nativeElement.querySelectorAll('button')].find((b: HTMLButtonElement) => b.textContent?.trim() === 'Request')?.disabled).toBe(true);
  });
});
