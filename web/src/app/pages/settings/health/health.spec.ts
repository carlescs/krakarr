import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import { HealthReport } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { HealthSettings } from './health';

const report = (issues: HealthReport['issues']): HealthReport => ({ checkedAt: '2026-11-25T10:00:00Z', issues });
const disk = {
  id: 'diskspace./data', check: 'diskspace', checkName: 'Disk space', severity: 'Error' as const,
  message: 'Only 4 GB of 1000 GB free on /data', link: '/settings/media', since: '2026-11-25T09:00:00Z',
};

describe('HealthSettings', () => {
  it('says so when everything is fine', async () => {
    setupTest({ api: mockApi({ health: async () => report([]) }) });
    const fixture = TestBed.createComponent(HealthSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '[data-healthy]')).toBe('No problems found.');
  });

  it('explains that the first check has not run yet', async () => {
    setupTest({ api: mockApi({ health: async () => ({ checkedAt: null, issues: [] }) }) });
    const fixture = TestBed.createComponent(HealthSettings);
    await settle(fixture);
    expect(text(fixture.nativeElement, '[data-pending]')).toContain('first check');
  });

  it('lists problems with a link to fix them', async () => {
    setupTest({
      api: mockApi({
        health: async () => report([disk, { ...disk, id: 'indexers.none', check: 'indexers', checkName: 'Indexers', severity: 'Warning', message: 'No indexer is enabled', link: '/indexers' }]),
      }),
    });
    const fixture = TestBed.createComponent(HealthSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '[data-summary]')).toContain('1 error, 1 warning');
    const item = el.querySelector('[data-issue="diskspace./data"]') as HTMLElement;
    expect(text(item)).toContain('Disk space');
    expect(text(item)).toContain('Only 4 GB of 1000 GB free on /data');
    expect(item.querySelector('a')?.getAttribute('href')).toBe('/settings/media');
  });

  it('checks again on demand and follows live updates', async () => {
    let current = report([disk]);
    const api = setupTest({ api: mockApi({ health: async () => current, checkHealth: async () => report([]) }) });
    const fixture = TestBed.createComponent(HealthSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    button(el, 'Check now').click();
    await settle(fixture);
    expect(api.checkHealth).toHaveBeenCalled();
    expect(el.querySelector('[data-healthy]')).not.toBeNull();

    current = report([disk]);
    TestBed.inject(Events).healthUpdated.next();
    await settle(fixture);
    expect(el.querySelector('[data-issue]')).not.toBeNull();
  });
});
