import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { Auth } from '../../core/auth';
import { button, mockApi, settle, setupTest, text, type } from '../../../testing/test-setup';
import { LoginPage } from './login';

async function render(api: ReturnType<typeof mockApi>, setupRequired: boolean) {
  setupTest({ api });
  TestBed.inject(Auth).status.set({ setupRequired, authenticated: false });
  const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  const fixture = TestBed.createComponent(LoginPage);
  await settle(fixture);
  return { fixture, navigate, el: fixture.nativeElement as HTMLElement };
}

describe('LoginPage', () => {
  it('signs in and opens the home page', async () => {
    const api = mockApi({ login: async () => undefined, authStatus: async () => ({ setupRequired: false, authenticated: true }) });
    const { fixture, navigate, el } = await render(api, false);
    expect(text(el, 'h1')).toBe('Sign in to Krakarr');
    expect(el.querySelector('#setupToken')).toBeNull();
    type(el, '#username', 'admin');
    type(el, '#password', 'secret');
    button(el, 'Sign in').click();
    await settle(fixture);
    expect(api.login).toHaveBeenCalledWith('admin', 'secret');
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('creates the first account with the setup token', async () => {
    const api = mockApi({ setup: async () => undefined, authStatus: async () => ({ setupRequired: false, authenticated: true }) });
    const { fixture, navigate, el } = await render(api, true);
    expect(text(el, 'h1')).toBe('Welcome to Krakarr');
    type(el, '#setupToken', ' token ');
    type(el, '#username', 'admin');
    type(el, '#password', 'password1');
    button(el, 'Create account').click();
    await settle(fixture);
    expect(api.setup).toHaveBeenCalledWith('admin', 'password1', 'token');
    expect(navigate).toHaveBeenCalledWith('/settings');
  });

  it('shows errors', async () => {
    const api = mockApi({ login: async () => { throw new Error('Invalid username or password'); } });
    const { fixture, navigate, el } = await render(api, false);
    button(el, 'Sign in').click();
    await settle(fixture);
    expect(text(el)).toContain('Invalid username or password');
    expect(navigate).not.toHaveBeenCalled();
  });
});
