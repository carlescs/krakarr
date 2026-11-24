/**
 * Browser side of passkey (WebAuthn) sign-in. The server sends options and expects responses in the WebAuthn JSON
 * form (binary fields as base64url); these helpers convert between that and the `navigator.credentials` API, without
 * relying on `PublicKeyCredential.parse*FromJSON`/`toJSON`, which older browsers lack.
 */
import { errorMessage } from './api';

/** Creation options as sent by `POST auth/passkeys/register/options`. */
export interface PasskeyCreationOptionsJson {
  challenge: string;
  rp: { id: string; name: string };
  user: { id: string; name: string; displayName: string };
  pubKeyCredParams: { type: 'public-key'; alg: number }[];
  timeout?: number;
  attestation?: AttestationConveyancePreference;
  authenticatorSelection?: AuthenticatorSelectionCriteria;
  excludeCredentials?: { type: 'public-key'; id: string; transports?: string[] }[];
}

/** Request options as sent by `POST auth/passkeys/login/options`. */
export interface PasskeyRequestOptionsJson {
  challenge: string;
  rpId: string;
  timeout?: number;
  userVerification?: UserVerificationRequirement;
}

/** A `PublicKeyCredential` with its binary fields as base64url. */
export interface PasskeyCredentialJson {
  id: string;
  rawId: string;
  type: string;
  response: {
    clientDataJSON: string;
    attestationObject?: string;
    transports?: string[];
    authenticatorData?: string;
    signature?: string;
    userHandle?: string | null;
  };
}

/** One of the signed-in user's passkeys. */
export interface Passkey { id: number; name: string; createdAt: string; lastUsedAt: string | null; }

/** True when this page can use passkeys: a browser with WebAuthn, over HTTPS or on localhost. */
export function passkeysSupported(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext && typeof window.PublicKeyCredential === 'function'
    && !!navigator.credentials;
}

export function toBase64Url(data: ArrayBuffer | ArrayBufferView): string {
  const bytes = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function fromBase64Url(value: string): ArrayBuffer {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes.buffer;
}

export function toCreationOptions(json: PasskeyCreationOptionsJson): PublicKeyCredentialCreationOptions {
  return {
    ...json,
    challenge: fromBase64Url(json.challenge),
    user: { ...json.user, id: fromBase64Url(json.user.id) },
    excludeCredentials: (json.excludeCredentials ?? []).map(c => ({
      type: c.type, id: fromBase64Url(c.id), transports: c.transports as AuthenticatorTransport[] | undefined,
    })),
  };
}

export function toRequestOptions(json: PasskeyRequestOptionsJson): PublicKeyCredentialRequestOptions {
  return { ...json, challenge: fromBase64Url(json.challenge), allowCredentials: [] };
}

/** Serializes the result of `navigator.credentials.create()` or `.get()` for the server. */
export function credentialToJson(credential: PublicKeyCredential): PasskeyCredentialJson {
  const response = credential.response;
  const json: PasskeyCredentialJson = {
    id: credential.id,
    rawId: toBase64Url(credential.rawId),
    type: credential.type,
    response: { clientDataJSON: toBase64Url(response.clientDataJSON) },
  };
  if ('attestationObject' in response) {
    const attestation = response as AuthenticatorAttestationResponse;
    json.response.attestationObject = toBase64Url(attestation.attestationObject);
    json.response.transports = typeof attestation.getTransports === 'function' ? attestation.getTransports() : [];
  } else {
    const assertion = response as AuthenticatorAssertionResponse;
    json.response.authenticatorData = toBase64Url(assertion.authenticatorData);
    json.response.signature = toBase64Url(assertion.signature);
    json.response.userHandle = assertion.userHandle ? toBase64Url(assertion.userHandle) : null;
  }
  return json;
}

/**
 * Message for a failed passkey ceremony (browser or API error), or null when the user simply cancelled. Browsers
 * report cancelling, timing out and "no passkey here" alike as `NotAllowedError`.
 */
export function passkeyErrorMessage(err: unknown): string | null {
  if (err instanceof DOMException) {
    switch (err.name) {
      case 'NotAllowedError':
      case 'AbortError': return null;
      case 'InvalidStateError': return 'This passkey is already registered.';
      case 'SecurityError': return 'Passkeys need Krakarr to be opened through a domain name over HTTPS.';
      case 'NotSupportedError': return 'This device has no passkey Krakarr can use.';
    }
  }
  return errorMessage(err);
}
