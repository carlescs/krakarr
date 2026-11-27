import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Api, errorMessage } from '../../../core/api';
import {
  PUSH_TOPICS, PushDevice, PushStatus, PushTopic, currentSubscription, deviceName, pushSupport, subscribe, subscriptionJson,
} from '../../../core/push';
import { Toasts } from '../../../core/toast';

/** Push notifications for the signed-in user: turn them on for this device, pick topics, and manage other devices. */
@Component({
  selector: 'kr-push-settings',
  imports: [DatePipe],
  templateUrl: './push.html',
  styleUrl: './push.scss',
})
export class PushSettings implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly support = pushSupport();
  protected readonly status = signal<PushStatus | null>(null);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  /** Endpoint of this browser's subscription, when it has one. */
  private readonly endpoint = signal<string | null>(null);
  protected readonly blocked = signal(this.support === 'ok' && Notification.permission === 'denied');

  protected readonly thisDevice = computed(() => this.status()?.devices.find(d => d.endpoint === this.endpoint()) ?? null);
  protected readonly otherDevices = computed(() => (this.status()?.devices ?? []).filter(d => d.endpoint !== this.endpoint()));
  protected readonly topics = computed(() => {
    const available = this.status()?.availableTopics ?? [];
    return PUSH_TOPICS.filter(t => available.includes(t.topic));
  });

  async ngOnInit() {
    try {
      this.status.set(await this.api.pushStatus());
      if (this.support === 'ok') this.endpoint.set((await currentSubscription())?.endpoint ?? null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  async enable() {
    const status = this.status();
    if (!status) return;
    this.busy.set(true);
    try {
      const subscription = await subscribe(status.publicKey);
      const device = await this.api.addPushDevice({ ...subscriptionJson(subscription), name: deviceName() });
      this.endpoint.set(device.endpoint);
      this.replace(device);
      this.toasts.success('Push notifications are on for this device');
    } catch (e) {
      if (e instanceof DOMException && e.name === 'NotAllowedError') {
        this.blocked.set(Notification.permission === 'denied');
        this.toasts.error('Notifications are blocked for Krakarr in this browser. Allow them in its site settings.');
      } else {
        this.toasts.error(errorMessage(e));
      }
    } finally {
      this.busy.set(false);
    }
  }

  async disable() {
    const device = this.thisDevice();
    if (!device) return;
    this.busy.set(true);
    try {
      await this.api.deletePushDevice(device.id);
      await (await currentSubscription())?.unsubscribe();
      this.endpoint.set(null);
      this.remove(device.id);
      this.toasts.success('Push notifications are off for this device');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async toggle(topic: PushTopic, on: boolean) {
    const device = this.thisDevice();
    if (!device) return;
    const topics = on ? [...device.topics, topic] : device.topics.filter(t => t !== topic);
    try {
      this.replace(await this.api.updatePushDevice(device.id, topics));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async test(device: PushDevice) {
    try {
      const result = await this.api.testPushDevice(device.id);
      if (result.delivered) {
        this.toasts.success(result.message);
      } else {
        this.toasts.error(result.message);
        this.status.set(await this.api.pushStatus()); // it may have been removed
      }
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async forget(device: PushDevice) {
    if (!confirm(`Stop sending notifications to "${device.name ?? 'this device'}"?`)) return;
    try {
      await this.api.deletePushDevice(device.id);
      this.remove(device.id);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  protected has(device: PushDevice, topic: PushTopic) {
    return device.topics.includes(topic);
  }

  private replace(device: PushDevice) {
    this.status.update(s => s && {
      ...s,
      devices: s.devices.some(d => d.id === device.id) ? s.devices.map(d => (d.id === device.id ? device : d)) : [...s.devices, device],
    });
  }

  private remove(id: number) {
    this.status.update(s => s && { ...s, devices: s.devices.filter(d => d.id !== id) });
  }
}
