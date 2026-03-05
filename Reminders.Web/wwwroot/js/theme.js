// Stable theme toggle with localStorage + cookie fallback
(function () {
    function getCookie(name) {
        const match = document.cookie.match(new RegExp('(^| )' + name + '=([^;]+)'));
        return match ? decodeURIComponent(match[2]) : null;
    }

    function setCookie(name, value) {
        document.cookie = `${name}=${encodeURIComponent(value)}; path=/; max-age=31536000; SameSite=Lax`;
    }

    let theme = 'light';

    try {
        const current = document.documentElement.getAttribute('data-theme');
        const stored = localStorage.getItem('theme');
        const cookieTheme = getCookie('theme');
        theme = stored || cookieTheme || current || 'light';

        document.documentElement.setAttribute('data-theme', theme);
        localStorage.setItem('theme', theme);
        setCookie('theme', theme);
    } catch {
        theme = getCookie('theme') || document.documentElement.getAttribute('data-theme') || 'light';
        document.documentElement.setAttribute('data-theme', theme);
        setCookie('theme', theme);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => updateToggleIcon(theme), { once: true });
    } else {
        updateToggleIcon(theme);
    }
})();

function toggleTheme() {
    const current = document.documentElement.getAttribute('data-theme') || 'light';
    const next = current === 'dark' ? 'light' : 'dark';

    document.documentElement.setAttribute('data-theme', next);

    try {
        localStorage.setItem('theme', next);
    } catch {}

    document.cookie = `theme=${encodeURIComponent(next)}; path=/; max-age=31536000; SameSite=Lax`;
    updateToggleIcon(next);
}

function updateToggleIcon(theme) {
    const btn = document.querySelector('.theme-toggle');
    if (btn) btn.textContent = theme === 'dark' ? '☀️' : '🌙';
}
