import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { UsersSettings } from './users';

const users = [
  { id: 1, username: 'tester', role: 'Admin', createdAt: '2026-01-01T00:00:00Z' },
  { id: 2, username: 'kim', role: 'User', createdAt: '2026-02-01T00:00:00Z' },
];

describe('UsersSettings', () => {
  it('lists users and only lets you delete others', async () => {
    setupTest({ api: mockApi({ users: async () => users }) });
    const fixture = TestBed.createComponent(UsersSettings);
    await settle(fixture);
    const rows = [...fixture.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];
    expect(text(rows[0])).toContain('tester (you)');
    expect(text(rows[0])).not.toContain('Delete');
    expect(text(rows[1])).toContain('kim');
    expect(text(rows[1])).toContain('Delete');
  });

  it('creates a user', async () => {
    const api = setupTest({ api: mockApi({ users: async () => users, createUser: async () => users[1] }) });
    const fixture = TestBed.createComponent(UsersSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, '+ Add user').click();
    await settle(fixture);
    type(el, '#user-name', ' lee ');
    type(el, '#user-password', 'password1');
    el.querySelectorAll<HTMLInputElement>('input[type="radio"]')[1].click(); // User, Manager, Admin
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.createUser).toHaveBeenCalledWith({ username: 'lee', password: 'password1', role: 'Manager' });
    expect(el.querySelector('#user-name')).toBeNull();
  });

  it('deletes a user after confirmation', async () => {
    const api = setupTest({ api: mockApi({ users: async () => users, deleteUser: async () => undefined }) });
    const fixture = TestBed.createComponent(UsersSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Delete').click();
    await settle(fixture);
    expect(text(el, 'h2')).toBe('Delete kim?');
    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Delete').click();
    await settle(fixture);
    expect(api.deleteUser).toHaveBeenCalledWith(2);
  });
});
