const CACHE_NAME = 'reminders-v2';

self.addEventListener('install', event => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(clients.claim());
});

self.addEventListener('push', event => {
    if (!event.data) return;

    let data;
    try {
        data = event.data.json();
    } catch {
        data = { title: 'Reminder', body: event.data.text() };
    }

    // Snooze and Done are only offered when the server sent the links for them.
    const actions = [];
    if (data.snoozeUrl) actions.push({ action: 'snooze', title: 'Snooze 10 min' });
    if (data.doneUrl) actions.push({ action: 'done', title: 'Mark done' });

    const options = {
        body: data.body || 'You have a reminder',
        icon: data.icon || '/icon-192.png',
        badge: data.badge || '/badge-72.png',
        vibrate: [200, 100, 200],
        tag: data.tag || 'reminder',
        renotify: true,
        data: { url: data.url || '/reminders', snoozeUrl: data.snoozeUrl || null, doneUrl: data.doneUrl || null },
        actions
    };

    event.waitUntil(
        self.registration.showNotification(data.title || 'Reminder', options)
    );
});

function openApp(url) {
    return clients.matchAll({ type: 'window', includeUncontrolled: true }).then(windowClients => {
        for (const client of windowClients) {
            if (client.url.startsWith(self.location.origin) && 'focus' in client) {
                client.navigate(url);
                return client.focus();
            }
        }
        return clients.openWindow(url);
    });
}

self.addEventListener('notificationclick', event => {
    const info = event.notification.data || {};
    event.notification.close();

    const actionUrl = event.action === 'snooze' ? info.snoozeUrl : event.action === 'done' ? info.doneUrl : null;

    if (actionUrl) {
        // Act in the background. If the request fails (offline, link expired), open the page
        // with the same buttons so the tap is not silently lost.
        event.waitUntil(
            fetch(actionUrl, { method: 'POST', headers: { 'Accept': 'application/json' } })
                .then(response => { if (!response.ok) throw new Error('action failed: ' + response.status); })
                .catch(() => openApp(info.url || '/reminders'))
        );
        return;
    }

    event.waitUntil(openApp(info.url || '/reminders'));
});
