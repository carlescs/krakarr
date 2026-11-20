import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { UserAccount, UserRole } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';

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
  template: `
    <div class="mb-4 flex flex-wrap items-center gap-3">
      <p class="flex-1 text-sm text-slate-400">People who can sign in to Krakarr. Their role decides what they can do.</p>
      <button class="btn-primary" (click)="edit(null)">+ Add user</button>
    </div>
    <div class="card overflow-x-auto">
      <table class="table">
        <thead><tr><th>Username</th><th class="w-32">Role</th><th class="w-40 max-sm:hidden">Created</th><th class="w-28"></th></tr></thead>
        <tbody>
          @for (u of users(); track u.id) {
            <tr>
              <td class="font-medium text-slate-100">
                {{ u.username }}
                @if (u.username === auth.status()?.username) { <span class="ml-1 text-xs font-normal text-slate-500">(you)</span> }
              </td>
              <td><span class="badge" [class]="roleInfo[u.role].badge" [title]="roleInfo[u.role].description">{{ roleInfo[u.role].label }}</span></td>
              <td class="whitespace-nowrap text-slate-400 max-sm:hidden">{{ u.createdAt | date: 'mediumDate' }}</td>
              <td class="whitespace-nowrap text-right">
                <button class="btn-ghost btn-sm" (click)="edit(u)">Edit</button>
                @if (u.username !== auth.status()?.username) {
                  <button class="btn-ghost btn-sm text-rose-300" (click)="confirmDelete.set(u)">Delete</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4" class="text-slate-500">{{ loading() ? 'Loading…' : 'No users' }}</td></tr>
          }
        </tbody>
      </table>
    </div>

    @if (form(); as f) {
      <kr-modal [title]="f.id ? 'Edit ' + f.username : 'Add user'" (closed)="form.set(null)">
        <form id="user-form" class="space-y-4" (ngSubmit)="save(f)">
          <div>
            <label class="label" for="user-name">Username</label>
            <input id="user-name" class="input" name="username" [(ngModel)]="f.username" autocomplete="off" required />
          </div>
          <div>
            <label class="label" for="user-password">{{ f.id ? 'New password' : 'Password' }}</label>
            <input id="user-password" class="input" name="password" type="password" [(ngModel)]="f.password" autocomplete="new-password"
                   [placeholder]="f.id ? 'Leave empty to keep the current one' : 'At least 8 characters'" />
            @if (f.id) { <p class="hint">Setting a new password signs the user out everywhere.</p> }
          </div>
          <fieldset>
            <legend class="label">Role</legend>
            <div class="space-y-2">
              @for (r of roles; track r) {
                <label class="flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2.5 transition"
                       [class]="f.role === r ? 'border-accent-400/60 bg-accent-500/5' : 'border-surface-700 hover:border-surface-600'">
                  <input type="radio" class="mt-1" name="role" [value]="r" [(ngModel)]="f.role" />
                  <span>
                    <span class="block text-sm font-medium text-slate-100">{{ roleInfo[r].label }}</span>
                    <span class="block text-xs text-slate-400">{{ roleInfo[r].description }}</span>
                  </span>
                </label>
              }
            </div>
          </fieldset>
        </form>
        <ng-container footer>
          <button class="btn-ghost" (click)="form.set(null)">Cancel</button>
          <button class="btn-primary" type="submit" form="user-form" [disabled]="saving() || !f.username.trim() || (!f.id && !f.password)">Save</button>
        </ng-container>
      </kr-modal>
    }

    @if (confirmDelete(); as u) {
      <kr-modal [title]="'Delete ' + u.username + '?'" (closed)="confirmDelete.set(null)">
        <p class="text-sm text-slate-300">{{ u.username }} will no longer be able to sign in, and is signed out right away.</p>
        <ng-container footer>
          <button class="btn-ghost" (click)="confirmDelete.set(null)">Cancel</button>
          <button class="btn-danger" (click)="remove(u)">Delete</button>
        </ng-container>
      </kr-modal>
    }
  `,
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
