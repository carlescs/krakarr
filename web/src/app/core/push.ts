/**
 * Browser side of push notifications (Web Push). The service worker (`public/sw.js`) shows them; these helpers
 * register it, subscribe this browser with the server's VAPID key and describe the device. Push works in Chrome, Edge,
 * Firefox and Safari, and on iPhone and iPad only once Krakarr is added to the Home Screen (iOS 16.4+).
 */
import { DestroyRef, Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import type { Api } from './api';
import { UserRole } from './models';

export type PushTopic = 'Available' | 'Imported' | 'Failed' | 'Requests' | 'RequestResults' | 'Health';

export interface PushDevice { id: number; name: string | null; endpoint: string; topics: PushTopic[]; createdAt: string; }

export interface PushStatus { publicKey: string; availableTopics: PushTopic[]; devices: PushDevice[]; }

export interface PushTestResult { delivered: boolean; message: string; }

/** What each topic means, in the order the settings list them. `role` is the least privileged role it applies to. */
export const PUSH_TOPICS: { topic: PushTopic; label: string; role: UserRole }[] = [
  { topic: 'Available', label: 'A title you follow is ready to watch', role: 'User' },
  { topic: 'RequestResults', label: 'Your request is approved or declined', role: 'User' },
  { topic: 'Requests', label: 'Someone requests a title', role: 'Manager' },
  { topic: 'Failed', label: 'A download fails to import', role: 'Manager' },
  { topic: 'Imported', label: 'Any download is imported', role: 'Manager' },
  { topic: 'Health', label: 'A health check reports an error', role: 'Admin' },
];

/**
 * Whether this browser can receive push notifications: `insecure` without HTTPS (or localhost), `install` on an
 * iPhone or iPad in Safari rather than the Home Screen app, `unsupported` when the browser lacks Web Push.
 */
export type PushSupport = 'ok' | 'insecure' | 'install' | 'unsupported';

export function pushSupport(): PushSupport {
  if (typeof window === 'undefined') return 'unsupported';
  if (!window.isSecureContext) return 'insecure';
  const supported = 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
  if (isIos() && !isStandalone()) return 'install';
  return supported ? 'ok' : 'unsupported';
}

function isIos(): boolean {
  return /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isStandalone(): boolean {
  return window.matchMedia?.('(display-mode: standalone)').matches || (navigator as { standalone?: boolean }).standalone === true;
}

/** A label for the device list, e.g. "Chrome on Android" or "Safari on iPhone (app)". */
export function deviceName(ua = navigator.userAgent): string {
  const browser = /EdgA?\//.test(ua) ? 'Edge'
    : /SamsungBrowser\//.test(ua) ? 'Samsung Internet'
    : /OPR\//.test(ua) ? 'Opera'
    : /Firefox\/|FxiOS\//.test(ua) ? 'Firefox'
    : /Chrome\/|CriOS\//.test(ua) ? 'Chrome'
    : /Safari\//.test(ua) ? 'Safari'
    : 'Browser';
  const os = /Android/.test(ua) ? 'Android'
    : /iPhone|iPod/.test(ua) ? 'iPhone'
    : /iPad/.test(ua) ? 'iPad'
    : /Windows/.test(ua) ? 'Windows'
    : /Mac OS X|Macintosh/.test(ua) ? (typeof navigator !== 'undefined' && navigator.maxTouchPoints > 1 ? 'iPad' : 'macOS')
    : /CrOS/.test(ua) ? 'ChromeOS'
    : /Linux/.test(ua) ? 'Linux'
    : '';
  const name = os ? `${browser} on ${os}` : browser;
  return typeof window !== 'undefined' && isStandalone() ? `${name} (app)` : name;
}

/** This browser's current push subscription, if it has one. Doesn't register the service worker. */
export async function currentSubscription(): Promise<PushSubscription | null> {
  const registration = await navigator.serviceWorker.getRegistration();
  return (await registration?.pushManager.getSubscription()) ?? null;
}

/**
 * Asks for permission (call it from a click: Safari requires that) and subscribes with the server's key. A
 * subscription made with another key (a backup from another instance was restored) is replaced.
 */
export async function subscribe(publicKey: string): Promise<PushSubscription> {
  if (await Notification.requestPermission() !== 'granted') {
    throw new DOMException('Notifications are blocked for Krakarr in this browser', 'NotAllowedError');
  }

  await navigator.serviceWorker.register('sw.js');
  const registration = await navigator.serviceWorker.ready;
  const key = base64UrlToBytes(publicKey);
  const existing = await registration.pushManager.getSubscription();
  if (existing && !sameKey(existing.options.applicationServerKey, key)) await existing.unsubscribe();
  return registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
}

/** The fields the server stores, from `PushSubscription.toJSON()`. */
export function subscriptionJson(subscription: PushSubscription): { endpoint: string; p256dh: string; auth: string } {
  const json = subscription.toJSON();
  return { endpoint: json.endpoint ?? subscription.endpoint, p256dh: json.keys?.['p256dh'] ?? '', auth: json.keys?.['auth'] ?? '' };
}

/**
 * Unsubscribes this browser and tells the server, so a shared device stops getting the account's notifications once
 * they sign out. Never throws: signing out must not depend on it.
 */
export async function forgetThisDevice(api: Api): Promise<void> {
  try {
    if (pushSupport() !== 'ok') return;
    const subscription = await currentSubscription();
    if (!subscription) return;
    await api.forgetPushDevice(subscription.endpoint).catch(() => undefined);
    await subscription.unsubscribe();
  } catch {
    // Nothing to clean up, or the browser refused: the server drops it once the push service reports it gone.
  }
}

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=');
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function sameKey(a: ArrayBuffer | null | undefined, b: Uint8Array): boolean {
  if (!a) return false;
  const bytes = new Uint8Array(a);
  return bytes.length === b.length && bytes.every((v, i) => v === b[i]);
}

/** Routes inside the open app when a notification is tapped (the service worker posts the link to it). */
@Injectable({ providedIn: 'root' })
export class PushNavigation {
  private readonly router = inject(Router);
  private listening = false;

  listen(destroyRef: DestroyRef): void {
    if (this.listening || typeof navigator === 'undefined' || !('serviceWorker' in navigator)) return;
    this.listening = true;
    const handler = (event: MessageEvent) => {
      const data = event.data as { type?: string; link?: unknown } | null;
      if (data?.type === 'krakarr:navigate' && typeof data.link === 'string' && data.link.startsWith('/')) {
        void this.router.navigateByUrl(data.link);
      }
    };
    navigator.serviceWorker.addEventListener('message', handler);
    destroyRef.onDestroy(() => {
      navigator.serviceWorker.removeEventListener('message', handler);
      this.listening = false;
    });
  }
}
