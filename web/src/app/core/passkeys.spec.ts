import { describe, expect, it } from 'vitest';
import { credentialToJson, fromBase64Url, passkeyErrorMessage, toBase64Url, toCreationOptions, toRequestOptions } from './passkeys';

const bytes = (...b: number[]) => new Uint8Array(b).buffer;

describe('passkeys', () => {
  it('round-trips base64url', () => {
    const data = new Uint8Array([0, 251, 255, 62, 63, 1]);
    expect(toBase64Url(data)).toBe('APv_Pj8B');
    expect(new Uint8Array(fromBase64Url('APv_Pj8B'))).toEqual(data);
    expect(new Uint8Array(fromBase64Url(toBase64Url(bytes(1, 2))))).toEqual(new Uint8Array([1, 2]));
  });

  it('decodes the server options', () => {
    const create = toCreationOptions({
      challenge: 'AQI', rp: { id: 'krakarr.example.com', name: 'Krakarr' }, user: { id: 'MQ', name: 'sam', displayName: 'sam' },
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }], excludeCredentials: [{ type: 'public-key', id: 'Aw', transports: ['usb'] }],
    });
    expect(new Uint8Array(create.challenge as ArrayBuffer)).toEqual(new Uint8Array([1, 2]));
    expect(new TextDecoder().decode(create.user.id as ArrayBuffer)).toBe('1');
    expect(create.excludeCredentials?.[0].transports).toEqual(['usb']);

    const get = toRequestOptions({ challenge: 'AQI', rpId: 'krakarr.example.com', userVerification: 'required' });
    expect(get.rpId).toBe('krakarr.example.com');
    expect(get.allowCredentials).toEqual([]);
  });

  it('encodes registration and sign-in responses', () => {
    const registration = credentialToJson({
      id: 'Aw', rawId: bytes(3), type: 'public-key',
      response: { clientDataJSON: bytes(1), attestationObject: bytes(2), getTransports: () => ['internal'] },
    } as unknown as PublicKeyCredential);
    expect(registration).toEqual({
      id: 'Aw', rawId: 'Aw', type: 'public-key', response: { clientDataJSON: 'AQ', attestationObject: 'Ag', transports: ['internal'] },
    });

    const assertion = credentialToJson({
      id: 'Aw', rawId: bytes(3), type: 'public-key',
      response: { clientDataJSON: bytes(1), authenticatorData: bytes(4), signature: bytes(5), userHandle: null },
    } as unknown as PublicKeyCredential);
    expect(assertion.response).toEqual({ clientDataJSON: 'AQ', authenticatorData: 'BA', signature: 'BQ', userHandle: null });
  });

  it('stays quiet when the user cancels', () => {
    expect(passkeyErrorMessage(new DOMException('cancelled', 'NotAllowedError'))).toBeNull();
    expect(passkeyErrorMessage(new DOMException('dup', 'InvalidStateError'))).toBe('This passkey is already registered.');
    expect(passkeyErrorMessage(new Error('boom'))).toBe('boom');
  });
});
