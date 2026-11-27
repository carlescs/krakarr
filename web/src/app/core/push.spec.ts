import { DestroyRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeSubscription, stubPush, unstubPush } from '../../testing/push';
import { mockApi, setupTest } from '../../testing/test-setup';
import { PushNavigation, deviceName, forgetThisDevice, pushSupport, subscribe, subscriptionJson } from './push';

describe('push', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    unstubPush();
  });

  it('names devices after the browser and system', () => {
    expect(deviceName('Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/131.0 Mobile Safari/537.36')).toBe('Chrome on Android');
    expect(deviceName('Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 Version/18.1 Mobile/15E148 Safari/604.1')).toBe('Safari on iPhone');
    expect(deviceName('Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36 Edg/131.0')).toBe('Edge on Windows');
    expect(deviceName('Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0')).toBe('Firefox on Linux');
    expect(deviceName('Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 SamsungBrowser/26.0 Chrome/122.0 Mobile Safari/537.36')).toBe('Samsung Internet on Android');
  });

  it('needs a secure context, and the Home Screen app on iOS', () => {
    vi.stubGlobal('isSecureContext', false);
    expect(pushSupport()).toBe('insecure');

    stubPush();
    expect(pushSupport()).toBe('ok');

    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) Safari/604.1');
    expect(pushSupport()).toBe('install');
  });

  it('subscribes with the server key and replaces a subscription made with another one', async () => {
    const old = fakeSubscription('https://fcm.googleapis.com/fcm/send/old', new Uint8Array([9]));
    const { pushManager, serviceWorker, notification } = stubPush({ subscription: old });

    const subscription = await subscribe('AQID'); // bytes 1, 2, 3

    expect(notification.requestPermission).toHaveBeenCalled();
    expect(serviceWorker.register).toHaveBeenCalledWith('sw.js');
    expect(old.unsubscribe).toHaveBeenCalled();
    const options = pushManager.subscribe.mock.calls[0] as unknown as [PushSubscriptionOptionsInit];
    expect(options[0].userVisibleOnly).toBe(true);
    expect([...(options[0].applicationServerKey as Uint8Array)]).toEqual([1, 2, 3]);
    expect(subscriptionJson(subscription as unknown as PushSubscription))
      .toEqual({ endpoint: 'https://fcm.googleapis.com/fcm/send/this', p256dh: 'BPub', auth: 'Auth' });
  });

  it('refuses when the user blocks notifications', async () => {
    const { notification, pushManager } = stubPush();
    notification.requestPermission.mockResolvedValue('denied');
    await expect(subscribe('AQID')).rejects.toMatchObject({ name: 'NotAllowedError' });
    expect(pushManager.subscribe).not.toHaveBeenCalled();
  });

  it('forgets this device on sign-out, and never throws', async () => {
    const subscription = fakeSubscription();
    stubPush({ subscription });
    const api = mockApi({ forgetPushDevice: async () => { throw new Error('offline'); } });

    await forgetThisDevice(api as never);

    expect(api.forgetPushDevice).toHaveBeenCalledWith(subscription.endpoint);
    expect(subscription.unsubscribe).toHaveBeenCalled();
  });

  it('routes inside the app when the service worker asks', () => {
    const { serviceWorker } = stubPush();
    setupTest();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
    const destroy: (() => void)[] = [];
    TestBed.inject(PushNavigation).listen({ onDestroy: (fn: () => void) => destroy.push(fn) } as unknown as DestroyRef);

    const handler = serviceWorker.addEventListener.mock.calls[0][1] as unknown as (e: MessageEvent) => void;
    handler(new MessageEvent('message', { data: { type: 'krakarr:navigate', link: '/movies/3' } }));
    handler(new MessageEvent('message', { data: { type: 'krakarr:navigate', link: 'https://evil.example' } }));
    expect(navigate).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith('/movies/3');

    destroy.forEach(fn => fn());
    expect(serviceWorker.removeEventListener).toHaveBeenCalledWith('message', handler);
  });
});
