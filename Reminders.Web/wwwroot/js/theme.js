// Stable theme toggle with localStorage + cookie fallback
(function () {
    function getCookie(name) {
        const match = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)'));
        return match ? decodeURIComponent(match[2]) : null;
    }

    function setCookie(name, value) {
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; SameSite=Lax`;
    }

    function readTheme() {
        const cookieTheme = getCookie('theme');
        const storageTheme = (() => { try { return localStorage.getItem('theme'); } catch { return null; } })();
        return cookieTheme || storageTheme || 'dark';
    }

    function applyTheme(theme) {
        document.documentElement.setAttribute('data-theme', theme);
        try { localStorage.setItem('theme', theme); } catch {}
        setCookie('theme', theme);
        updateToggleIcon(theme);
    }

    window.enforceTheme = function () {
        applyTheme(readTheme());
    }

    applyTheme(readTheme());

    window.addEventListener('pageshow', () => applyTheme(readTheme()));
    window.addEventListener('popstate', () => applyTheme(readTheme()));
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') applyTheme(readTheme());
    });
})();

function toggleTheme() {
    const current = document.documentElement.getAttribute('data-theme') || 'dark';
    const next = current === 'dark' ? 'light' : 'dark';

    document.documentElement.setAttribute('data-theme', next);
    try { localStorage.setItem('theme', next); } catch {}
    document.cookie = `theme=${encodeURIComponent(next)}; path=/; max-age=31536000; SameSite=Lax`;
    updateToggleIcon(next);
}

function updateToggleIcon(theme) {
    const btn = document.querySelector('.theme-toggle');
    if (btn) btn.textContent = theme === 'dark' ? '☀️' : '🌙';
}
