import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../core/events';
import { Indexer } from '../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { IndexersPage } from './indexers';

const indexer: Indexer = {
  id: 3, name: 'Nyaa', protocol: 'Torznab', baseUrl: 'https://nyaa.test', apiPath: '/api', apiKey: 'k', movieCategories: [2000], tvCategories: [5000],
  enabled: true, enableRss: false, priority: 10, minimumSeeders: 1, defaultLanguage: null,
};
const caps = { success: true, capabilities: { categories: [{ id: 2000, name: 'Movies' }, { id: 2040, name: 'Movies/HD', parentId: 2000 }, { id: 5000, name: 'TV' }] } };

describe('IndexersPage', () => {
  it('adds an indexer', async () => {
    const api = setupTest({
      api: mockApi({ indexers: async () => [], languages: async () => ({ all: [], preferred: [] }), saveIndexer: async () => indexer }),
    });
    const fixture = TestBed.createComponent(IndexersPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain('No indexers yet');

    button(el, '+ Add indexer').click();
    await settle(fixture);
    const inputs = el.querySelectorAll<HTMLInputElement>('[role="dialog"] input.input');
    inputs[0].value = 'Jackett';
    inputs[0].dispatchEvent(new Event('input'));
    type(el, 'input[placeholder^="https://"]', 'https://jackett.test');
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveIndexer).toHaveBeenCalledWith(expect.objectContaining({ id: 0, name: 'Jackett', baseUrl: 'https://jackett.test', movieCategories: [2000] }));
    expect(el.querySelector('[role="dialog"]')).toBeNull();
  });

  it('loads the categories of an indexer being edited and tests it', async () => {
    const api = setupTest({
      api: mockApi({
        indexers: async () => [indexer],
        languages: async () => ({ all: [], preferred: [] }),
        indexerCapabilities: async () => caps,
        testIndexer: async () => ({ ...caps, capabilities: { ...caps.capabilities, movieSearchAvailable: true, movieSearchParams: ['q', 'imdbid'], tvSearchAvailable: false } }),
      }),
    });
    const fixture = TestBed.createComponent(IndexersPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'tbody')).toContain('Nyaa');
    button(el, 'Edit').click();
    await settle(fixture);
    expect(api.indexerCapabilities).toHaveBeenCalledWith(expect.objectContaining({ id: 3 }));
    expect(text(el)).toContain('3 categories from the indexer.');
    expect(el.querySelectorAll('kr-category-picker').length).toBe(2);

    button(el, 'Test').click();
    await settle(fixture);
    expect(text(el)).toContain('Connection OK. Movie search: yes (q, imdbid) · TV search: no');
  });

  it('imports the chosen indexers from Prowlarr', async () => {
    const api = setupTest({
      api: mockApi({
        indexers: async () => [],
        languages: async () => ({ all: [], preferred: [] }),
        prowlarrIndexers: async () => ({
          success: true,
          indexers: [
            { id: 1, name: 'Nyaa', protocol: 'torrent', supported: true, enabled: true, priority: 25 },
            { id: 2, name: 'Old', protocol: 'torrent', supported: true, enabled: true, priority: 25, existingId: 9 },
            { id: 3, name: 'NZBgeek', protocol: 'usenet', supported: false, enabled: true, priority: 25 },
          ],
        }),
        importProwlarrIndexers: async () => ({ added: 1, updated: 0 }),
      }),
    });
    const fixture = TestBed.createComponent(IndexersPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Import from Prowlarr').click();
    await settle(fixture);
    type(el, 'input[placeholder^="http://prowlarr"]', 'http://prowlarr:9696');
    const key = el.querySelectorAll<HTMLInputElement>('[role="dialog"] input.input')[1];
    key.value = 'secret';
    key.dispatchEvent(new Event('input'));
    await settle(fixture);
    button(el, 'Connect').click();
    await settle(fixture);
    expect(api.prowlarrIndexers).toHaveBeenCalledWith('http://prowlarr:9696', 'secret');
    expect(text(el, '[role="dialog"]')).toContain('Already added');
    expect(text(el, '[role="dialog"]')).toContain('Usenet, not supported');
    expect(el.querySelector<HTMLInputElement>('input[aria-label="NZBgeek"]')!.disabled).toBe(true);

    button(el, 'Import 1').click();
    await settle(fixture);
    expect(api.importProwlarrIndexers).toHaveBeenCalledWith('http://prowlarr:9696', 'secret', [1]);
    expect(el.querySelector('[role="dialog"]')).toBeNull();
  });

  it('shows which indexers are failing and refreshes on live updates', async () => {
    const until = new Date(Date.now() + 10 * 60_000).toISOString();
    let statuses = [{ indexerId: 3, indexerName: 'Nyaa', failures: 2, disabledUntil: until, lastFailure: until, lastError: 'HTTP 503' }];
    setupTest({
      api: mockApi({ indexers: async () => [indexer], languages: async () => ({ all: [], preferred: [] }), indexerStatus: async () => statuses }),
    });
    const fixture = TestBed.createComponent(IndexersPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '[data-failing]')).toContain('Failing skipped until');
    expect(text(el, '[data-failing]')).toContain('2× HTTP 503');
    expect(el.querySelector('[data-failing]')?.getAttribute('title')).toContain('Failed 2 times in a row: HTTP 503');

    statuses = [];
    TestBed.inject(Events).indexerStatusUpdated.next();
    await settle(fixture);
    expect(el.querySelector('[data-failing]')).toBeNull();
  });
});
