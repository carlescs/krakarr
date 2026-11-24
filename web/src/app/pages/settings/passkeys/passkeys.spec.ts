import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Toasts } from '../../../core/toast';
import { stubPasskeys } from '../../../../testing/passkeys';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { PasskeySettings } from './passkeys';

const phone = { id: 1, name: 'Phone', createdAt: '2026-01-02T00:00:00Z', lastUsedAt: null };
const options = {
  challenge: 'AQI', rp: { id: 'localhost', name: 'Krakarr' }, user: { id: 'MQ', name: 'tester', displayName: 'tester' },
  pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
};

describe('PasskeySettings', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('lists, adds and removes passkeys', async () => {
    const credentials = stubPasskeys({ attestationObject: new Uint8Array([2]).buffer, getTransports: () => ['internal'] });
    const api = setupTest({
      api: mockApi({
        passkeys: async () => [phone],
        passkeyRegistrationOptions: async () => options,
        addPasskey: async () => ({ id: 2, name: 'YubiKey', createdAt: '2026-02-01T00:00:00Z', lastUsedAt: null }),
        deletePasskey: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(PasskeySettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '.passkey-name')).toBe('Phone');
    expect(text(el)).toContain('Never used');

    type(el, '#pk-name', ' YubiKey ');
    type(el, '#pk-password', 'secret');
    await settle(fixture);
    button(el, 'Add passkey').click();
    await settle(fixture);
    expect(api.passkeyRegistrationOptions).toHaveBeenCalledWith('secret');
    expect(credentials.create).toHaveBeenCalled();
    expect(api.addPasskey).toHaveBeenCalledWith('YubiKey', {
      id: 'Aw', rawId: 'Aw', type: 'public-key', response: { clientDataJSON: 'AQ', attestationObject: 'Ag', transports: ['internal'] },
    });
    expect([...el.querySelectorAll('.passkey-name')].map(n => n.textContent?.trim())).toEqual(['Phone', 'YubiKey']);

    vi.stubGlobal('confirm', () => true);
    button(el, 'Remove Phone').click();
    await settle(fixture);
    expect(api.deletePasskey).toHaveBeenCalledWith(1);
    expect([...el.querySelectorAll('.passkey-name')].map(n => n.textContent?.trim())).toEqual(['YubiKey']);
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['Passkey added', 'Passkey removed']);
  });

  it('says nothing when the user cancels the browser prompt', async () => {
    const credentials = stubPasskeys();
    credentials.create.mockRejectedValue(new DOMException('cancelled', 'NotAllowedError'));
    const api = setupTest({ api: mockApi({ passkeys: async () => [], passkeyRegistrationOptions: async () => options }) });
    const fixture = TestBed.createComponent(PasskeySettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el)).toContain("You haven't added a passkey yet.");
    type(el, '#pk-name', 'Phone');
    type(el, '#pk-password', 'secret');
    await settle(fixture);
    button(el, 'Add passkey').click();
    await settle(fixture);
    expect(api.addPasskey).not.toHaveBeenCalled();
    expect(TestBed.inject(Toasts).items()).toEqual([]);
  });

  it('renames a passkey', async () => {
    stubPasskeys();
    const api = setupTest({ api: mockApi({ passkeys: async () => [phone], renamePasskey: async () => ({ ...phone, name: 'Work phone' }) }) });
    const fixture = TestBed.createComponent(PasskeySettings);
    await settle(fixture);
    vi.stubGlobal('prompt', () => ' Work phone ');
    button(fixture.nativeElement as HTMLElement, 'Rename Phone').click();
    await settle(fixture);
    expect(api.renamePasskey).toHaveBeenCalledWith(1, 'Work phone');
    expect(text(fixture.nativeElement as HTMLElement, '.passkey-name')).toBe('Work phone');
  });

  it('explains when passkeys are unavailable', async () => {
    vi.stubGlobal('isSecureContext', false);
    setupTest({ api: mockApi({ passkeys: async () => [] }) });
    const fixture = TestBed.createComponent(PasskeySettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('#pk-name')).toBeNull();
    expect(text(el)).toContain('Passkeys need Krakarr to be opened through a domain name over HTTPS');
  });
});
