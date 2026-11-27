import { vi } from 'vitest';

/** A browser push subscription as `PushManager` hands it out. */
export function fakeSubscription(endpoint = 'https://fcm.googleapis.com/fcm/send/this', key = new Uint8Array([1, 2, 3])) {
  return {
    endpoint,
    options: { applicationServerKey: key.buffer },
    toJSON: () => ({ endpoint, keys: { p256dh: 'BPub', auth: 'Auth' } }),
    unsubscribe: vi.fn(async () => true),
  };
}

/**
 * Makes `pushSupport()` 'ok' (a secure context with service workers, PushManager and Notification) with `subscription`
 * as this browser's current one. Undo with `vi.unstubAllGlobals()`.
 */
export function stubPush(options: { subscription?: ReturnType<typeof fakeSubscription> | null; permission?: NotificationPermission } = {}) {
  let current = options.subscription ?? null;
  const pushManager = {
    getSubscription: vi.fn(async () => current),
    subscribe: vi.fn(async () => (current = fakeSubscription())),
  };
  const registration = { pushManager };
  const serviceWorker = {
    getRegistration: vi.fn(async () => registration),
    register: vi.fn(async () => registration),
    ready: Promise.resolve(registration),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const notification = { permission: options.permission ?? 'default', requestPermission: vi.fn(async () => 'granted') };
  vi.stubGlobal('isSecureContext', true);
  vi.stubGlobal('PushManager', class {});
  vi.stubGlobal('Notification', notification);
  Object.defineProperty(navigator, 'serviceWorker', { value: serviceWorker, configurable: true });
  return { pushManager, serviceWorker, notification };
}

/** Removes the `navigator.serviceWorker` stub (globals are reset by `vi.unstubAllGlobals()`). */
export function unstubPush() {
  delete (navigator as { serviceWorker?: unknown }).serviceWorker;
}
