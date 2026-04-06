// Push notification registration helper

async function registerPushSubscription(vapidPublicKey) {
    if (!('serviceWorker' in navigator) || !('PushManager' in window)) {
        console.warn('Push notifications are not supported in this browser.');
        return false;
    }

    try {
        const registration = await navigator.serviceWorker.register('/sw.js');
        console.log('Service worker registered:', registration.scope);

        const permission = await Notification.requestPermission();
        if (permission !== 'granted') {
            console.warn('Push notification permission denied.');
            return false;
        }

        const applicationServerKey = urlBase64ToUint8Array(vapidPublicKey);
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey
        });

        const subJson = subscription.toJSON();
        const response = await fetch('/api/push/subscribe', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                endpoint: subJson.endpoint,
                p256dh: subJson.keys.p256dh,
                auth: subJson.keys.auth
            })
        });

        if (!response.ok) {
            console.error('Failed to save push subscription on server:', response.status);
            return false;
        }

        console.log('Push subscription registered successfully.');
        return true;
    } catch (err) {
        console.error('Error registering push subscription:', err);
        return false;
    }
}

async function unregisterPushSubscription() {
    if (!('serviceWorker' in navigator)) return;

    try {
        const registration = await navigator.serviceWorker.ready;
        const subscription = await registration.pushManager.getSubscription();

        if (!subscription) return;

        const endpoint = subscription.endpoint;

        await fetch('/api/push/unsubscribe', {
            method: 'DELETE',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ endpoint })
        });

        await subscription.unsubscribe();
        console.log('Push subscription removed.');
    } catch (err) {
        console.error('Error unregistering push subscription:', err);
    }
}

// Helper: convert VAPID base64 URL-safe key to Uint8Array
function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const rawData = window.atob(base64);
    return Uint8Array.from([...rawData].map(char => char.charCodeAt(0)));
}

window.scrollToFirstValidationError = function () {
    const run = () => {
        const invalidInput = document.querySelector('[aria-invalid="true"], .input-validation-error');
        const validationMsg = document.querySelector('.validation-message, .field-validation-error');
        const target = invalidInput || validationMsg;
        if (!target) return;

        const modalScrollable = target.closest('.modal-card, .form-modal');
        if (modalScrollable) {
            const top = target.getBoundingClientRect().top - modalScrollable.getBoundingClientRect().top + modalScrollable.scrollTop - 24;
            modalScrollable.scrollTo({ top: Math.max(0, top), behavior: 'smooth' });
        } else {
            target.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }

        const input = target.matches('input,select,textarea')
            ? target
            : target.closest('.form-group')?.querySelector('input,select,textarea');
        if (input) setTimeout(() => input.focus(), 120);
    };

    // Run twice to catch first validation render
    requestAnimationFrame(run);
    setTimeout(run, 90);
};

window.canRegisterPushVapid = function () {
    const ua = navigator.userAgent || '';
    const isIOSSafari = /iP(ad|hone|od)/.test(ua) && /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS/.test(ua);
    if (isIOSSafari) return false;

    // Do not hard-block on isSecureContext here; let actual registration report precise failure.
    return !!('serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window);
};
