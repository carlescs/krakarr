import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { UserAccount, UserRole } from '../../../core/models';
import { Toasts } from '../../../core/toast';
import { Modal } from '../../../shared/modal/modal';

export const ROLE_INFO: Record<UserRole, { label: string; description: string; badge: string }> = {
  Admin: { label: 'Admin', description: 'Everything, including settings, indexers and users.', badge: 'bg-accent-500/15 text-accent-300' },
  Manager: { label: 'Manager', description: 'Adds, searches and removes movies and series, and manages downloads. No settings or users.', badge: 'bg-violet-500/15 text-violet-300' },
  User: { label: 'User', description: 'Browses Discover and the library. Can’t change anything.', badge: 'bg-surface-700 text-slate-300' },
};

interface UserForm { id: number; username: string; role: UserRole; password: string; }

/** Admin-only list of accounts with create, edit (role, username, password reset) and delete. */
@Component({
  selector: 'kr-users-settings',
  imports: [FormsModule, Modal, DatePipe],
  templateUrl: './users.html',
  styleUrl: './users.scss',
})
export class UsersSettings implements OnInit {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly roleInfo = ROLE_INFO;
  protected readonly roles: UserRole[] = ['User', 'Manager', 'Admin'];
  protected readonly users = signal<UserAccount[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly form = signal<UserForm | null>(null);
  protected readonly confirmDelete = signal<UserAccount | null>(null);

  async ngOnInit() { await this.load(); }

  private async load() {
    try { this.users.set(await this.api.users()); }
    catch (e) { this.toasts.error(errorMessage(e)); }
    finally { this.loading.set(false); }
  }

  edit(u: UserAccount | null) {
    this.form.set(u ? { id: u.id, username: u.username, role: u.role, password: '' } : { id: 0, username: '', role: 'User', password: '' });
  }

  async save(f: UserForm) {
    this.saving.set(true);
    try {
      const username = f.username.trim();
      if (f.id) {
        await this.api.updateUser(f.id, { username, role: f.role, password: f.password || null });
        // Renaming yourself changes the name shown in the header.
        if (this.users().find(u => u.id === f.id)?.username === this.auth.status()?.username) await this.auth.refresh();
      } else {
        await this.api.createUser({ username, password: f.password, role: f.role });
      }
      this.form.set(null);
      this.toasts.success('User saved');
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async remove(u: UserAccount) {
    try {
      await this.api.deleteUser(u.id);
      this.confirmDelete.set(null);
      this.toasts.success(`${u.username} deleted`);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
