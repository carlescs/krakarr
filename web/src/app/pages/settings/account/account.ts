import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Toasts } from '../../../core/toast';
import { PasskeySettings } from '../passkeys/passkeys';
import { ROLE_INFO } from '../users/users';

/** The signed-in user's own username, password and passkeys. */
@Component({
  selector: 'kr-account-settings',
  imports: [FormsModule, PasskeySettings],
  templateUrl: './account.html',
  styleUrl: './account.scss',
})
export class AccountSettings {
  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly roleInfo = ROLE_INFO;
  protected readonly saving = signal(false);
  protected readonly revoking = signal(false);

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

  async revokeOthers() {
    this.revoking.set(true);
    try {
      await this.api.revokeOtherSessions();
      this.toasts.success('Signed out of every other session');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.revoking.set(false);
    }
  }
}
