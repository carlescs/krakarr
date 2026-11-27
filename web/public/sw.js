// Krakarr's service worker. It only shows push notifications and opens the page they point to: nothing is cached,
// since the app needs the server anyway. Push payloads come from WebPushSender (src/Krakarr.Host/Push).
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch {
    data = { body: event.data ? event.data.text() : '' };
  }
  event.waitUntil(self.registration.showNotification(data.title || 'Krakarr', {
    body: data.body || undefined,
    icon: 'icon-192.png',
    tag: data.tag || undefined,
    renotify: Boolean(data.tag),
    data: { link: data.link || '/' },
  }));
});

// Links are app routes ("/movies/12"); they are resolved against the scope so a URL base (/krakarr/) is kept.
self.addEventListener('notificationclick', event => {
  event.notification.close();
  const link = (event.notification.data && event.notification.data.link) || '/';
  const url = new URL(link.replace(/^\//, ''), self.registration.scope).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const open = windows.find(w => w.url.startsWith(self.registration.scope));
    if (open) {
      await open.focus();
      open.postMessage({ type: 'krakarr:navigate', link }); // the app routes without reloading
      return;
    }
    await self.clients.openWindow(url);
  })());
});

// The push service replaced the subscription (keys rotated or it expired): register the new one with the server.
self.addEventListener('pushsubscriptionchange', event => {
  event.waitUntil((async () => {
    const subscription = event.newSubscription
      || (event.oldSubscription && await self.registration.pushManager.subscribe(event.oldSubscription.options));
    if (!subscription) return;
    const json = subscription.toJSON();
    await fetch(new URL('api/v1/push/devices', self.registration.scope), {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth }),
    });
  })());
});
