import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { Api } from './api';
import { AuthStatus, UserRole } from './models';

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly api = inject(Api);
  readonly status = signal<AuthStatus | null>(null);
  readonly role = computed<UserRole>(() => this.status()?.role ?? 'User');
  /** Admins: settings, indexers, users and everything else. */
  readonly isAdmin = computed(() => this.role() === 'Admin');
  /** Admins and managers: manage the library and downloads. Plain users only browse. */
  readonly isStaff = computed(() => this.role() === 'Admin' || this.role() === 'Manager');

  async refresh(): Promise<AuthStatus> {
    const s = await this.api.authStatus();
    this.status.set(s);
    return s;
  }

  async login(username: string, password: string) {
    await this.api.login(username, password);
    await this.refresh();
  }

  async setup(username: string, password: string, setupToken: string) {
    await this.api.setup(username, password, setupToken);
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
  return status?.authenticated ? router.createUrlTree(['/']) : true;
};

const RANK: Record<UserRole, number> = { Admin: 0, Manager: 1, User: 2 };

/** Route guard for pages that need at least <paramref name="min"/>; others land on Discover. */
export function roleGuard(min: UserRole): CanActivateFn {
  return async () => {
    const auth = inject(Auth);
    const router = inject(Router);
    const status = auth.status() ?? (await auth.refresh().catch(() => null));
    if (!status?.authenticated) return router.createUrlTree(['/login']);
    return RANK[status.role ?? 'User'] <= RANK[min] ? true : router.createUrlTree(['/discover']);
  };
}

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
