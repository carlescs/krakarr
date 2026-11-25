import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, backupUrl, errorMessage } from '../../../core/api';
import { BytesPipe } from '../../../core/format';
import { BackupInfo, BackupList, BackupSettings } from '../../../core/models';
import { Toasts } from '../../../core/toast';

/**
 * Admin-only backups: the schedule and retention, the backups in /config/Backups (download, delete, restore) and
 * restoring from an uploaded zip. A restore is staged on the server and applied the next time Krakarr starts.
 */
@Component({
  selector: 'kr-backup-settings',
  imports: [DatePipe, FormsModule, BytesPipe],
  templateUrl: './backups.html',
  styleUrl: './backups.scss',
})
export class BackupSettingsPanel implements OnInit {
  private readonly api = inject(Api);
  protected readonly backupUrl = backupUrl;
  private readonly toasts = inject(Toasts);

  protected readonly settings = signal<BackupSettings | null>(null);
  protected readonly list = signal<BackupList | null>(null);
  protected readonly saving = signal(false);
  protected readonly creating = signal(false);
  /** A restore request (upload or stored backup) is in flight. */
  protected readonly staging = signal(false);

  async ngOnInit() {
    try {
      const [settings, list] = await Promise.all([this.api.settings('backup'), this.api.backups()]);
      this.settings.set(settings);
      this.list.set(list);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private async reload() {
    this.list.set(await this.api.backups());
  }

  async save() {
    const s = this.settings();
    if (!s || this.saving()) return;
    this.saving.set(true);
    try {
      this.settings.set(await this.api.saveSettings('backup', s));
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async backUpNow() {
    this.creating.set(true);
    try {
      const created = await this.api.createBackup();
      this.toasts.success(`Created ${created.name}`);
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.creating.set(false);
    }
  }

  async remove(b: BackupInfo) {
    if (!confirm(`Delete ${b.name}?`)) return;
    try {
      await this.api.deleteBackup(b.name);
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async restore(b: BackupInfo) {
    if (!confirm(`Restore ${b.name}? It replaces the current database and settings the next time Krakarr starts.`)) return;
    await this.stage(() => this.api.restoreBackup(b.name));
  }

  async upload(input: HTMLInputElement) {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    if (!confirm(`Restore ${file.name}? It replaces the current database and settings the next time Krakarr starts.`)) return;
    await this.stage(() => this.api.uploadBackup(file));
  }

  private async stage(request: () => Promise<void>) {
    this.staging.set(true);
    try {
      await request();
      this.toasts.success('Backup ready to restore. Restart Krakarr to apply it.');
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.staging.set(false);
    }
  }

  async cancelRestore() {
    try {
      await this.api.cancelRestore();
      await this.reload();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
