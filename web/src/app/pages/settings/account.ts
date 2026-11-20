import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Toasts } from '../../core/toast';
import { ROLE_INFO } from './users';

/** The signed-in user's own username and password. */
@Component({
  selector: 'kr-account-settings',
  imports: [FormsModule],
  template: `
    <section class="card max-w-2xl space-y-4 p-4 sm:p-5">
      <div class="flex flex-wrap items-center gap-2">
        <h2 class="flex-1 font-semibold text-white">Your account</h2>
        <span class="badge" [class]="roleInfo[auth.role()].badge" [title]="roleInfo[auth.role()].description">{{ roleInfo[auth.role()].label }}</span>
      </div>
      <form class="space-y-4" (ngSubmit)="save()">
        <div class="grid gap-4 sm:grid-cols-2">
          <div>
            <label class="label" for="acc-username">Username</label>
            <input id="acc-username" class="input" name="username" [(ngModel)]="username" autocomplete="username" required />
          </div>
          <div>
            <label class="label" for="acc-current">Current password</label>
            <input id="acc-current" class="input" name="current" type="password" [(ngModel)]="currentPassword" autocomplete="current-password" required />
          </div>
          <div>
            <label class="label" for="acc-new">New password</label>
            <input id="acc-new" class="input" name="new" type="password" [(ngModel)]="newPassword" autocomplete="new-password" placeholder="Leave empty to keep" />
          </div>
          <div>
            <label class="label" for="acc-confirm">Confirm new password</label>
            <input id="acc-confirm" class="input" name="confirm" type="password" [(ngModel)]="confirmPassword" autocomplete="new-password" />
          </div>
        </div>
        <p class="hint">Changing your password signs you out on every other device.</p>
        <div class="flex justify-end">
          <button class="btn-primary" type="submit" [disabled]="saving() || !currentPassword || !username.trim()">Save</button>
        </div>
      </form>
    </section>
  `,
})
export class AccountSettings {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly roleInfo = ROLE_INFO;
  protected readonly saving = signal(false);

  protected username = this.auth.status()?.username ?? '';
  protected currentPassword = '';
  protected newPassword = '';
  protected confirmPassword = '';

  async save() {
    if (this.newPassword !== this.confirmPassword) {
      this.toasts.error('The new passwords do not match');
      return;
    }

    this.saving.set(true);
    try {
      await this.api.changeCredentials(this.username.trim(), this.currentPassword, this.newPassword);
      await this.auth.refresh();
      this.currentPassword = this.newPassword = this.confirmPassword = '';
      this.toasts.success('Account updated');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }
}
