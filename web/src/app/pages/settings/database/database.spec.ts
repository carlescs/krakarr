import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DatabaseInfo } from '../../../core/models';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { DatabaseSettings } from './database';

const info = (overrides: Partial<DatabaseInfo> = {}): DatabaseInfo => ({
  provider: 'sqlite', location: '/config/krakarr.db', pinned: [], pending: null, pendingEnvironment: null, lastSwitch: null, oldDatabase: null, ...overrides,
});

async function render(api = mockApi({ database: async () => info() })) {
  setupTest({ api });
  const fixture = TestBed.createComponent(DatabaseSettings);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('DatabaseSettings', () => {
  afterEach(() => vi.restoreAllMocks());

  it('shows the current database and offers PostgreSQL when on SQLite', async () => {
    const { el } = await render();
    expect(text(el, '[data-current]')).toContain('SQLite');
    expect(text(el, '[data-current]')).toContain('/config/krakarr.db');
    expect(el.querySelector('[data-postgres-fields]')).not.toBeNull();
    expect(button(el, 'Switch on next restart').disabled).toBe(true);
  });

  it('offers SQLite when on PostgreSQL', async () => {
    const { el } = await render(mockApi({ database: async () => info({ provider: 'postgres', location: 'krakarr@db:5432/krakarr' }) }));
    expect(el.querySelector('[data-sqlite-fields]')).not.toBeNull();
  });

  it('tests the connection, then stages the switch', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let current = info();
    const api = mockApi({
      database: async () => current,
      testDatabase: async () => ({ ok: true, empty: true, message: 'Connected to PostgreSQL 17. The database is empty.' }),
      switchDatabase: async () => {
        current = info({ pending: { provider: 'postgres', location: 'krakarr@db:5432/krakarr', created: '2026-11-26T10:00:00Z', deleteSource: null } });
        return { message: 'ok', environment: [] };
      },
    });
    const { el, fixture } = await render(api);
    type(el, '#db-host', 'db');
    type(el, '#db-name', 'krakarr');
    type(el, '#db-user', 'krakarr');
    type(el, '#db-password', 'secret');
    await settle(fixture);

    button(el, 'Test connection').click();
    await settle(fixture);
    const target = { provider: 'postgres', host: 'db', port: 5432, database: 'krakarr', username: 'krakarr', password: 'secret' };
    expect(api.testDatabase).toHaveBeenCalledWith(target);
    expect(text(el, '[data-test-result]')).toContain('empty');

    button(el, 'Switch on next restart').click();
    await settle(fixture);
    expect(api.switchDatabase).toHaveBeenCalledWith({ ...target, deleteSource: false });
    expect(text(el, '[data-pending]')).toContain('Restart Krakarr');
    expect(text(el, '[data-pending]')).toContain('left as it was');
    expect(el.querySelector('[data-assistant]')).toBeNull();
  });

  it('can delete the SQLite file once the data is copied', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    let current = info();
    const api = mockApi({
      database: async () => current,
      testDatabase: async () => ({ ok: true, empty: true, message: 'empty' }),
      switchDatabase: async () => {
        current = info({ pending: { provider: 'postgres', location: 'krakarr@db:5432/krakarr', created: '2026-11-26T10:00:00Z', deleteSource: '/config/krakarr.db' } });
        return { message: 'ok', environment: [] };
      },
    });
    const { el, fixture } = await render(api);
    const box = el.querySelector('#db-delete-source') as HTMLInputElement;
    box.click();
    await settle(fixture);
    button(el, 'Test connection').click();
    await settle(fixture);
    button(el, 'Switch on next restart').click();
    await settle(fixture);
    expect(confirm.mock.calls[0][0]).toContain('deleted');
    expect(api.switchDatabase).toHaveBeenCalledWith(expect.objectContaining({ deleteSource: true }));
    expect(text(el, '[data-delete-pending]')).toContain('/config/krakarr.db');
  });

  it('deletes the old SQLite file left behind by a switch', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    let current = info({ provider: 'postgres', location: 'krakarr@db:5432/krakarr',
      oldDatabase: { file: '/config/krakarr.db', size: 2048, switchedAt: '2026-11-26T10:00:00Z' } });
    const api = mockApi({ database: async () => current, deleteOldDatabase: async () => { current = { ...current, oldDatabase: null }; } });
    const { el, fixture } = await render(api);
    expect(text(el, '[data-old-database]')).toContain('/config/krakarr.db');
    expect(text(el, '[data-old-database]')).toContain('PostgreSQL');

    button(el, 'Delete old database').click();
    await settle(fixture);
    expect(api.deleteOldDatabase).toHaveBeenCalled();
    expect(el.querySelector('[data-old-database]')).toBeNull();
  });

  it('only offers to delete the old database when leaving SQLite', async () => {
    const { el } = await render(mockApi({ database: async () => info({ provider: 'postgres', location: 'krakarr@db:5432/krakarr' }) }));
    expect(el.querySelector('#db-delete-source')).toBeNull();
  });

  it('needs a new test after the form changes, and an empty database to switch', async () => {
    const api = mockApi({
      database: async () => info(),
      testDatabase: async () => ({ ok: true, empty: false, message: 'already holds Krakarr data' }),
    });
    const { el, fixture } = await render(api);
    button(el, 'Test connection').click();
    await settle(fixture);
    expect(text(el, '[data-test-result]')).toContain('already holds');
    expect(button(el, 'Switch on next restart').disabled).toBe(true);

    type(el, '#db-host', 'other');
    await settle(fixture);
    expect(el.querySelector('[data-test-result]')).toBeNull();
  });

  it('lists the environment variables to set, and cancels the switch', async () => {
    let current = info({
      pinned: ['Database__Provider'],
      pending: { provider: 'postgres', location: 'krakarr@db:5432/krakarr', created: '2026-11-26T10:00:00Z', deleteSource: null },
      pendingEnvironment: [{ name: 'Database__Provider', value: 'postgres' }, { name: 'Database__ConnectionString', value: 'Host=db;Password=<password>' }],
    });
    const api = mockApi({ database: async () => current, cancelDatabaseSwitch: async () => { current = info(); } });
    const { el, fixture } = await render(api);
    expect(text(el, '[data-pinned]')).toContain('Database__Provider');
    expect(text(el, '[data-environment]')).toContain('Database__ConnectionString=Host=db;Password=<password>');

    button(el, 'Cancel switch').click();
    await settle(fixture);
    expect(api.cancelDatabaseSwitch).toHaveBeenCalled();
    expect(el.querySelector('[data-pending]')).toBeNull();
  });

  it('shows the outcome of the last switch with the rows per table', async () => {
    const { el } = await render(mockApi({
      database: async () => info({
        provider: 'postgres',
        lastSwitch: { succeeded: true, message: 'Switched to krakarr@db:5432/krakarr: copied 1,200 rows in 2 tables.', at: '2026-11-26T10:00:00Z',
          tables: [{ table: 'Movies', rows: 200, truncated: 0 }, { table: 'Episodes', rows: 1000, truncated: 3 }] },
      }),
    }));
    expect(text(el, '[data-last-switch]')).toContain('copied 1,200 rows');
    expect(text(el, '[data-table="Episodes"]')).toContain('3 shortened');
  });
});
