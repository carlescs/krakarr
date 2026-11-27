import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { BackupList } from '../../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { BackupSettingsPanel } from './backups';

const backup = { name: 'krakarr_backup_20261125_101500_manual.zip', type: 'Manual' as const, size: 2048, created: '2026-11-25T10:15:00Z' };
const list = (overrides: Partial<BackupList> = {}): BackupList => ({ backups: [backup], restorePending: false, lastRestore: null, provider: 'sqlite', ...overrides });

async function render(api = mockApi({ settings: async () => ({ intervalDays: 7, retentionCount: 5 }), backups: async () => list() })) {
  setupTest({ api });
  const fixture = TestBed.createComponent(BackupSettingsPanel);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('BackupSettingsPanel', () => {
  afterEach(() => vi.restoreAllMocks());

  it('lists backups with a download link', async () => {
    const { el } = await render();
    const row = el.querySelector(`[data-backup="${backup.name}"]`) as HTMLElement;
    expect(text(row)).toContain('Manual');
    expect(row.querySelector('a')?.getAttribute('href')).toBe(`api/v1/system/backups/${backup.name}`);
    expect(el.querySelector('[data-pending]')).toBeNull();
    expect(el.querySelector('[data-postgres]')).toBeNull();
  });

  it('saves the schedule', async () => {
    const api = mockApi({
      settings: async () => ({ intervalDays: 7, retentionCount: 5 }), backups: async () => list(),
      saveSettings: async (_: string, v: unknown) => v,
    });
    const { el, fixture } = await render(api);
    type(el, '#backup-retention', '3');
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('backup', { intervalDays: 7, retentionCount: 3 });
  });

  it('backs up now and refreshes the list', async () => {
    let current = list({ backups: [] });
    const api = mockApi({
      settings: async () => ({ intervalDays: 7, retentionCount: 5 }), backups: async () => current,
      createBackup: async () => { current = list(); return backup; },
    });
    const { el, fixture } = await render(api);
    expect(text(el)).toContain('No backups yet.');
    button(el, 'Back up now').click();
    await settle(fixture);
    expect(api.createBackup).toHaveBeenCalled();
    expect(el.querySelector(`[data-backup="${backup.name}"]`)).not.toBeNull();
  });

  it('stages a stored backup for restore and can cancel it', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let current = list();
    const api = mockApi({
      settings: async () => ({ intervalDays: 7, retentionCount: 5 }), backups: async () => current,
      restoreBackup: async () => { current = list({ restorePending: true }); },
      cancelRestore: async () => { current = list(); },
    });
    const { el, fixture } = await render(api);
    button(el, 'Restore').click();
    await settle(fixture);
    expect(api.restoreBackup).toHaveBeenCalledWith(backup.name);
    expect(text(el, '[data-pending]')).toContain('Restart Krakarr');

    button(el, 'Cancel restore').click();
    await settle(fixture);
    expect(el.querySelector('[data-pending]')).toBeNull();
  });

  it('uploads a backup file to restore', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const api = mockApi({
      settings: async () => ({ intervalDays: 7, retentionCount: 5 }), backups: async () => list(),
      uploadBackup: async () => undefined,
    });
    const { el, fixture } = await render(api);
    const input = el.querySelector('[data-upload]') as HTMLInputElement;
    const file = new File(['zip'], 'backup.zip', { type: 'application/zip' });
    Object.defineProperty(input, 'files', { value: [file], configurable: true });
    input.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.uploadBackup).toHaveBeenCalledWith(file);
  });

  it('shows the outcome of the last restore and the PostgreSQL note', async () => {
    const { el } = await render(mockApi({
      settings: async () => ({ intervalDays: 7, retentionCount: 5 }),
      backups: async () => list({ provider: 'postgres', lastRestore: { succeeded: false, message: 'The backup could not be restored: bad', at: '2026-11-25T10:00:00Z' } }),
    }));
    expect(text(el, '[data-last-restore]')).toContain('could not be restored');
    expect(text(el, '[data-postgres]')).toContain('pg_dump');
  });
});
