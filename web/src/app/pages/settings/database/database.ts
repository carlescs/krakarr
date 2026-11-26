import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { DatabaseInfo, DatabaseProvider, DatabaseTargetRequest, DatabaseTestResult } from '../../../core/models';
import { Toasts } from '../../../core/toast';

const PROVIDER_NAMES: Record<DatabaseProvider, string> = { sqlite: 'SQLite', postgres: 'PostgreSQL' };

/**
 * Admin-only assistant that moves Krakarr to another database (SQLite ⇄ PostgreSQL): pick the new database, test the
 * connection, then stage the switch. The next start copies every table into it and points config.json at it.
 */
@Component({
  selector: 'kr-database-settings',
  imports: [DatePipe, DecimalPipe, FormsModule],
  templateUrl: './database.html',
  styleUrl: './database.scss',
})
export class DatabaseSettings implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly providerNames = PROVIDER_NAMES;
  protected readonly info = signal<DatabaseInfo | null>(null);
  protected readonly form = signal<DatabaseTargetRequest>({ provider: 'postgres', port: 5432, file: 'krakarr.db' });
  /** Result of the last connection test; cleared whenever the form changes. */
  protected readonly test = signal<DatabaseTestResult | null>(null);
  protected readonly testing = signal(false);
  protected readonly staging = signal(false);
  protected readonly canSwitch = computed(() => !!this.test()?.ok && !!this.test()?.empty);
  /** Delete the current SQLite file once the copy has succeeded. Off by default: the file is the way back. */
  protected readonly deleteSource = signal(false);

  async ngOnInit() {
    try {
      const info = await this.api.database();
      this.info.set(info);
      // Offer the other provider first: that's the usual reason to be here.
      this.form.update(f => ({ ...f, provider: info.provider === 'postgres' ? 'sqlite' : 'postgres' }));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private async reload() {
    this.info.set(await this.api.database());
  }

  protected edit(change: Partial<DatabaseTargetRequest>) {
    this.form.update(f => ({ ...f, ...change }));
    this.test.set(null);
  }

  /** Only the fields of the chosen provider are sent. */
  private request(): DatabaseTargetRequest {
    const f = this.form();
    return f.provider === 'postgres'
      ? { provider: 'postgres', host: f.host, port: f.port, database: f.database, username: f.username, password: f.password }
      : { provider: 'sqlite', file: f.file };
  }

  async runTest() {
    this.testing.set(true);
    this.test.set(null);
    try {
      this.test.set(await this.api.testDatabase(this.request()));
    } catch (e) {
      this.test.set({ ok: false, empty: false, message: errorMessage(e) });
    } finally {
      this.testing.set(false);
    }
  }

  async stage() {
    if (!this.canSwitch()) return;
    const name = PROVIDER_NAMES[this.form().provider];
    const deleting = this.info()?.provider === 'sqlite' && this.deleteSource()
      ? ' Once the copy succeeds, the SQLite file is deleted and can only come back from a backup.' : '';
    if (!confirm(`Switch to ${name}? Krakarr copies all its data into the new database the next time it starts.${deleting}`)) return;
    this.staging.set(true);
    try {
      const leavingSqlite = this.info()?.provider === 'sqlite';
      await this.api.switchDatabase({ ...this.request(), deleteSource: leavingSqlite && this.deleteSource() });
      this.toasts.success('Switch ready. Restart Krakarr to copy the data.');
      this.test.set(null);
      this.form.update(f => ({ ...f, password: '' }));
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.staging.set(false);
    }
  }

  async cancel() {
    try {
      await this.api.cancelDatabaseSwitch();
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
