import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';

@Component({
  selector: 'kr-login',
  imports: [FormsModule],
  templateUrl: './login.html',
  styleUrl: './login.scss',
})
export class LoginPage implements OnInit {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  protected readonly setup = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected username = '';
  protected password = '';
  protected setupToken = '';

  async ngOnInit() {
    const status = this.auth.status() ?? (await this.auth.refresh());
    this.setup.set(status.setupRequired);
  }

  async submit() {
    this.busy.set(true);
    this.error.set(null);
    try {
      if (this.setup()) await this.auth.setup(this.username, this.password, this.setupToken.trim());
      else await this.auth.login(this.username, this.password);
      this.router.navigateByUrl(this.setup() ? '/settings' : '/');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
