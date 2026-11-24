import { vi } from 'vitest';

/** Makes `passkeysSupported()` true and `navigator.credentials` answer with `credential`. Undo with `vi.unstubAllGlobals()`. */
export function stubPasskeys(response: Record<string, unknown> = {}) {
  class FakeCredential {
    id = 'Aw';
    rawId = new Uint8Array([3]).buffer;
    type = 'public-key';
    response = { clientDataJSON: new Uint8Array([1]).buffer, ...response };
  }
  const credentials = { create: vi.fn(async () => new FakeCredential()), get: vi.fn(async () => new FakeCredential()) };
  vi.stubGlobal('PublicKeyCredential', FakeCredential);
  vi.stubGlobal('isSecureContext', true);
  Object.defineProperty(navigator, 'credentials', { value: credentials, configurable: true });
  return credentials;
}
