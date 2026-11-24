import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { relativeTime } from '../../../core/format';
import { Passkey, credentialToJson, passkeyErrorMessage, passkeysSupported, toCreationOptions } from '../../../core/passkeys';
import { Toasts } from '../../../core/toast';

/** The signed-in user's passkeys: add (after confirming the password), rename and remove. */
@Component({
  selector: 'kr-passkey-settings',
  imports: [FormsModule, DatePipe],
  templateUrl: './passkeys.html',
  styleUrl: './passkeys.scss',
})
export class PasskeySettings implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly supported = passkeysSupported();
  protected readonly relativeTime = relativeTime;
  protected readonly passkeys = signal<Passkey[]>([]);
  protected readonly loading = signal(true);
  protected readonly adding = signal(false);
  protected name = '';
  protected password = '';

  async ngOnInit() {
    try {
      this.passkeys.set(await this.api.passkeys());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  async add() {
    this.adding.set(true);
    try {
      const options = await this.api.passkeyRegistrationOptions(this.password);
      const credential = await navigator.credentials.create({ publicKey: toCreationOptions(options) });
      if (!(credential instanceof PublicKeyCredential)) return;
      const added = await this.api.addPasskey(this.name.trim(), credentialToJson(credential));
      this.passkeys.update(list => [...list, added]);
      this.name = this.password = '';
      this.toasts.success('Passkey added');
    } catch (e) {
      const message = passkeyErrorMessage(e);
      if (message) this.toasts.error(message);
    } finally {
      this.adding.set(false);
    }
  }

  async rename(passkey: Passkey) {
    const name = prompt('Passkey name', passkey.name)?.trim();
    if (!name || name === passkey.name) return;
    try {
      const updated = await this.api.renamePasskey(passkey.id, name);
      this.passkeys.update(list => list.map(p => (p.id === updated.id ? updated : p)));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async remove(passkey: Passkey) {
    if (!confirm(`Remove the passkey "${passkey.name}"? You won't be able to sign in with it any more.`)) return;
    try {
      await this.api.deletePasskey(passkey.id);
      this.passkeys.update(list => list.filter(p => p.id !== passkey.id));
      this.toasts.success('Passkey removed');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
