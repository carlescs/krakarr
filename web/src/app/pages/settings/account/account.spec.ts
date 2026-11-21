import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { Toasts } from '../../../core/toast';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { AccountSettings } from './account';

describe('AccountSettings', () => {
  it('changes the username and password', async () => {
    const api = setupTest({
      role: 'Manager',
      api: mockApi({ changeCredentials: async () => undefined, authStatus: async () => ({ setupRequired: false, authenticated: true, username: 'sam', role: 'Manager' }) }),
    });
    const fixture = TestBed.createComponent(AccountSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '.badge')).toBe('Manager');
    expect((el.querySelector('#acc-username') as HTMLInputElement).value).toBe('tester');

    type(el, '#acc-username', ' sam ');
    type(el, '#acc-current', 'old');
    type(el, '#acc-new', 'new-password');
    type(el, '#acc-confirm', 'new-password');
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.changeCredentials).toHaveBeenCalledWith('sam', 'old', 'new-password');
    expect(api.authStatus).toHaveBeenCalled();
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['Account updated']);
  });

  it('rejects mismatched passwords', async () => {
    const api = setupTest();
    const fixture = TestBed.createComponent(AccountSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    type(el, '#acc-current', 'old');
    type(el, '#acc-new', 'one');
    type(el, '#acc-confirm', 'two');
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.changeCredentials).not.toHaveBeenCalled();
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['The new passwords do not match']);
  });

  it('signs out the other sessions', async () => {
    const api = setupTest({ api: mockApi({ revokeOtherSessions: async () => undefined }) });
    const fixture = TestBed.createComponent(AccountSettings);
    await settle(fixture);
    button(fixture.nativeElement as HTMLElement, 'Sign out other sessions').click();
    await settle(fixture);
    expect(api.revokeOtherSessions).toHaveBeenCalled();
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['Signed out of every other session']);
  });
});
