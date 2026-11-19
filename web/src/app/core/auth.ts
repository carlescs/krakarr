import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Api } from './api';
import { AuthStatus } from './models';

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  readonly status = signal<AuthStatus | null>(null);

  async refresh(): Promise<AuthStatus> {
    const s = await this.api.authStatus();
    this.status.set(s);
    return s;
  }

  async login(username: string, password: string) {
    await this.api.login(username, password);
    await this.refresh();
  }

  async setup(username: string, password: string) {
    await this.api.setup(username, password);
    await this.refresh();
  }

  async logout() {
    await this.api.logout();
    this.status.set({ setupRequired: false, authenticated: false });
  }

  markLoggedOut() {
    const s = this.status();
    this.status.set({ setupRequired: s?.setupRequired ?? false, authenticated: false });
  }
}

export const authGuard: CanActivateFn = async () => {
  const auth = inject(Auth);
  const router = inject(Router);
  const status = auth.status() ?? (await auth.refresh().catch(() => null));
  if (status?.authenticated) return true;
  return router.createUrlTree(['/login']);
};

export const guestGuard: CanActivateFn = async () => {
  const auth = inject(Auth);
  const router = inject(Router);
  const status = auth.status() ?? (await auth.refresh().catch(() => null));
  return status?.authenticated ? router.createUrlTree(['/movies']) : true;
};

/** Sends the user to the login page whenever an API call returns 401. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const router = inject(Router);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !req.url.includes('/auth/')) {
        auth.markLoggedOut();
        router.navigateByUrl('/login');
      }
      return throwError(() => err);
    }),
  );
};
