// Theme toggle — persists via localStorage
(function () {
    const current = document.documentElement.getAttribute('data-theme');
    const stored = localStorage.getItem('theme');
    const theme = stored || current || 'light';
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('theme', theme);
    updateToggleIcon(theme);

    // Keep icon in sync after Blazor re-renders/nav updates
    const observer = new MutationObserver(() => updateToggleIcon(document.documentElement.getAttribute('data-theme') || theme));
    observer.observe(document.body, { childList: true, subtree: true });
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
