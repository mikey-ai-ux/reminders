// Lightweight theme toggle — no MutationObserver
(function () {
    const current = document.documentElement.getAttribute('data-theme');
    const stored = localStorage.getItem('theme');
    const theme = stored || current || 'light';

    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('theme', theme);

    // Set icon once DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => updateToggleIcon(theme), { once: true });
    } else {
        updateToggleIcon(theme);
    }

    // Re-sync icon on navigation restores
    window.addEventListener('pageshow', () => updateToggleIcon(document.documentElement.getAttribute('data-theme') || theme));
})();

function toggleTheme() {
    const current = document.documentElement.getAttribute('data-theme') || 'light';
    const next = current === 'dark' ? 'light' : 'dark';

    document.documentElement.setAttribute('data-theme', next);
    localStorage.setItem('theme', next);
    updateToggleIcon(next);
}

function updateToggleIcon(theme) {
    const btn = document.querySelector('.theme-toggle');
    if (btn) btn.textContent = theme === 'dark' ? '☀️' : '🌙';
}
