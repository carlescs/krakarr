import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { AddSeriesPage } from './add-series';

describe('AddSeriesPage', () => {
  it('searches and offers to add titles that are not in the library', async () => {
    const api = setupTest({ api: mockApi({ lookupSeries: async () => [{ tmdbId: 1396, title: 'Breaking Bad', year: 2008, network: 'AMC', status: 'Ended' }, { tmdbId: 60059, title: 'Better Call Saul', year: 2015, seriesId: 9 }] }) });
    const fixture = TestBed.createComponent(AddSeriesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', ' breaking ');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(api.lookupSeries).toHaveBeenCalledWith('breaking');
    expect(el.querySelectorAll('h2').length).toBe(2);
    expect(el.querySelector('a.btn-secondary[href="/series/9"]')?.textContent).toContain('In library');

    button(el, 'Add').click();
    await settle(fixture);
    expect(el.querySelector('kr-add-series-modal')).not.toBeNull();
  });

  it('lets plain users request titles instead', async () => {
    const api = setupTest({ role: 'User', api: mockApi({ lookupSeries: async () => [{ tmdbId: 1396, title: 'Breaking Bad', year: 2008 }], createRequest: async () => ({ id: 1, tmdbId: 1396, title: 'x' }) }) });
    const fixture = TestBed.createComponent(AddSeriesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h1')).toBe('Search series');
    type(el, 'input[name="term"]', 'x');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    button(el, 'Request').click();
    await settle(fixture);
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Request').click();
    await settle(fixture);
    expect(api.createRequest).toHaveBeenCalledWith('Series', 1396);
    expect(el.querySelector('a[href="/requests"]')?.textContent).toContain('Requested');
  });

  it('says when nothing was found', async () => {
    setupTest({ api: mockApi({ lookupSeries: async () => [] }) });
    const fixture = TestBed.createComponent(AddSeriesPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, 'input[name="term"]', 'zzz');
    await settle(fixture);
    button(el, 'Search').click();
    await settle(fixture);
    expect(text(el)).toContain('No series found.');
  });
});
