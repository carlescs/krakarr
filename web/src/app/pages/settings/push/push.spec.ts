import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PushDevice, PushStatus } from '../../../core/push';
import { Toasts } from '../../../core/toast';
import { fakeSubscription, stubPush, unstubPush } from '../../../../testing/push';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { PushSettings } from './push';

const thisEndpoint = 'https://fcm.googleapis.com/fcm/send/this';
const laptop: PushDevice = { id: 1, name: 'Firefox on Linux', endpoint: 'https://updates.push.services.mozilla.com/wpush/v2/x', topics: ['Available'], createdAt: '2026-11-01T00:00:00Z' };
const phone: PushDevice = { id: 2, name: 'Chrome on Android', endpoint: thisEndpoint, topics: ['Available', 'Failed'], createdAt: '2026-11-20T00:00:00Z' };
const status = (devices: PushDevice[], availableTopics: PushStatus['availableTopics'] = ['Available', 'Imported', 'Failed', 'Requests', 'RequestResults']): PushStatus =>
  ({ publicKey: 'AQID', availableTopics, devices });

describe('PushSettings', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    unstubPush();
  });

  const topicBoxes = (el: HTMLElement) => [...el.querySelectorAll<HTMLInputElement>('.push-topic')];

  it('turns notifications on for this device', async () => {
    const { pushManager } = stubPush();
    const api = setupTest({
      api: mockApi({ pushStatus: async () => status([laptop]), addPushDevice: async () => ({ ...phone, topics: ['Available', 'Failed', 'Requests', 'RequestResults'] }) }),
      role: 'Manager',
    });
    const fixture = TestBed.createComponent(PushSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, '.push-device')).toBe('Firefox on Linux');

    button(el, 'Turn on for this device').click();
    await settle(fixture);

    expect(pushManager.subscribe).toHaveBeenCalled();
    expect(api.addPushDevice).toHaveBeenCalledWith(expect.objectContaining({ endpoint: thisEndpoint, p256dh: 'BPub', auth: 'Auth' }));
    expect(text(el)).toContain('On for this device (Chrome on Android)');
    // Only the topics the server offers this role, ticked as stored.
    expect(topicBoxes(el).map(b => [b.dataset['topic'], b.checked])).toEqual([
      ['Available', true], ['RequestResults', true], ['Requests', true], ['Failed', true], ['Imported', false],
    ]);
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['Push notifications are on for this device']);
  });

  it('changes topics, sends a test and turns off', async () => {
    const subscription = fakeSubscription(thisEndpoint);
    stubPush({ subscription });
    const api = setupTest({
      api: mockApi({
        pushStatus: async () => status([laptop, phone], ['Available', 'RequestResults']),
        updatePushDevice: async (_: number, topics: string[]) => ({ ...phone, topics }),
        testPushDevice: async () => ({ delivered: true, message: 'Test notification sent' }),
        deletePushDevice: async () => undefined,
      }),
      role: 'User',
    });
    const fixture = TestBed.createComponent(PushSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(topicBoxes(el).map(b => b.dataset['topic'])).toEqual(['Available', 'RequestResults']);

    const results = topicBoxes(el)[1];
    results.checked = true;
    results.dispatchEvent(new Event('change'));
    await settle(fixture);
    expect(api.updatePushDevice).toHaveBeenCalledWith(2, ['Available', 'Failed', 'RequestResults']);

    button(el, 'Send test').click();
    await settle(fixture);
    expect(api.testPushDevice).toHaveBeenCalledWith(2);

    button(el, 'Turn off').click();
    await settle(fixture);
    expect(api.deletePushDevice).toHaveBeenCalledWith(2);
    expect(subscription.unsubscribe).toHaveBeenCalled();
    expect(text(el)).toContain('Turn on for this device');
    expect(TestBed.inject(Toasts).items().map(t => t.message)).toEqual(['Test notification sent', 'Push notifications are off for this device']);
  });

  it('removes another device', async () => {
    stubPush();
    const api = setupTest({ api: mockApi({ pushStatus: async () => status([laptop]), deletePushDevice: async () => undefined }) });
    const fixture = TestBed.createComponent(PushSettings);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    vi.stubGlobal('confirm', () => true);
    button(el, 'Remove Firefox on Linux').click();
    await settle(fixture);
    expect(api.deletePushDevice).toHaveBeenCalledWith(1);
    expect(el.querySelector('.push-device')).toBeNull();
  });

  it('explains when the browser is blocked or unsupported', async () => {
    stubPush({ permission: 'denied' });
    setupTest({ api: mockApi({ pushStatus: async () => status([]) }) });
    let fixture = TestBed.createComponent(PushSettings);
    await settle(fixture);
    expect(text(fixture.nativeElement)).toContain('Notifications are blocked for Krakarr in this browser');
    expect(() => button(fixture.nativeElement, 'Turn on for this device')).toThrow();

    vi.stubGlobal('isSecureContext', false);
    TestBed.resetTestingModule();
    setupTest({ api: mockApi({ pushStatus: async () => status([]) }) });
    fixture = TestBed.createComponent(PushSettings);
    await settle(fixture);
    expect(text(fixture.nativeElement, '.push-unsupported')).toContain('HTTPS');
  });
});
