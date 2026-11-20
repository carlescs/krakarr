import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { errorMessage } from '../core/api';
import { Auth } from '../core/auth';

@Component({
  selector: 'kr-login',
  imports: [FormsModule],
  template: `
    <div class="flex min-h-full items-center justify-center bg-[radial-gradient(ellipse_at_top,_var(--color-surface-800),_var(--color-surface-950)_60%)] px-4">
      <form class="card w-full max-w-sm p-6 sm:p-8" (ngSubmit)="submit()">
        <div class="mb-6 flex flex-col items-center gap-3 text-center">
          <img src="favicon.svg" alt="" class="size-12" />
          <h1 class="text-xl font-semibold text-white">{{ setup() ? 'Welcome to Krakarr' : 'Sign in to Krakarr' }}</h1>
          @if (setup()) {
            <p class="text-sm text-slate-400">Create the administrator account to get started.</p>
          }
        </div>
        <div class="space-y-4">
          <div>
            <label class="label" for="username">Username</label>
            <input id="username" class="input" name="username" autocomplete="username" [(ngModel)]="username" required />
          </div>
          <div>
            <label class="label" for="password">Password</label>
            <input id="password" class="input" type="password" name="password" [autocomplete]="setup() ? 'new-password' : 'current-password'"
                   [(ngModel)]="password" required />
            @if (setup()) { <p class="hint">At least 8 characters.</p> }
          </div>
          @if (error()) {
            <p class="rounded-lg bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{{ error() }}</p>
          }
          <button class="btn-primary w-full" type="submit" [disabled]="busy()">
            {{ busy() ? 'Please wait…' : setup() ? 'Create account' : 'Sign in' }}
          </button>
        </div>
      </form>
    </div>
  `,
})
export class LoginPage implements OnInit {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  protected readonly setup = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected username = '';
  protected password = '';

  async ngOnInit() {
    const status = this.auth.status() ?? (await this.auth.refresh());
    this.setup.set(status.setupRequired);
  }

  async submit() {
    this.busy.set(true);
    this.error.set(null);
    try {
      if (this.setup()) await this.auth.setup(this.username, this.password);
      else await this.auth.login(this.username, this.password);
      this.router.navigateByUrl(this.setup() ? '/settings' : '/movies');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
