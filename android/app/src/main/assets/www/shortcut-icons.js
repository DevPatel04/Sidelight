// Shared shortcut glyphs for the phone panel and desktop settings preview.
(() => {
  const svg = (body) => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${body}</svg>`;
  const icons = {
    premiere: { mark: 'Pr', color: '#a78bfa' },
    aftereffects: { mark: 'Ae', color: '#67e8f9' },
    spotify: { color: '#1ed760', svg: '<circle cx="12" cy="12" r="10" fill="#1ed760" stroke="none"/><path d="M6.4 9.3c3.8-1.1 8.3-.7 11.1 1" fill="none" stroke="#102016" stroke-width="1.5"/><path d="M7.1 12.1c3.1-.9 6.9-.5 9.4.8" fill="none" stroke="#102016" stroke-width="1.4"/><path d="M7.8 14.7c2.5-.7 5.3-.3 7.4.8" fill="none" stroke="#102016" stroke-width="1.3"/>' },
    terminal: { color: '#d1d5db', svg: '<rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><path d="m7.5 9 3 3-3 3M13 15h3.5"/>' },
    discord: { color: '#818cf8', svg: '<path d="M7 7.3a15 15 0 0 1 10 0l2.1 8.2a1.5 1.5 0 0 1-2.2 1.7l-2.1-1.2a7 7 0 0 1-5.6 0l-2.1 1.2a1.5 1.5 0 0 1-2.2-1.7L7 7.3Z"/><path d="M8.5 11.3h.01M15.5 11.3h.01M9 14.5c1.8 1.1 4.2 1.1 6 0"/>' },
    lark: { color: '#38bdf8', svg: '<path d="M4 6.5A2.5 2.5 0 0 1 6.5 4h8A2.5 2.5 0 0 1 17 6.5v4a2.5 2.5 0 0 1-2.5 2.5H9l-3.8 3v-3.3A2.4 2.4 0 0 1 4 10.5Z"/><path d="M10 16.5A2.5 2.5 0 0 0 12.5 19H16l3 2v-3a2.5 2.5 0 0 0 1-2v-2"/>' },
    settings: { color: '#cbd5e1', svg: '<path d="M12 8.2a3.8 3.8 0 1 0 0 7.6 3.8 3.8 0 0 0 0-7.6Z"/><path d="m19.4 13.5 1.1.9-1.2 2.1-1.4-.5a7.8 7.8 0 0 1-1.6.9l-.3 1.5h-2.4l-.3-1.5a7.8 7.8 0 0 1-1.6-.9l-1.4.5-1.2-2.1 1.1-.9a7 7 0 0 1 0-1.8l-1.1-.9 1.2-2.1 1.4.5a7.8 7.8 0 0 1 1.6-.9l.3-1.5h2.4l.3 1.5a7.8 7.8 0 0 1 1.6.9l1.4-.5 1.2 2.1-1.1.9a7 7 0 0 1 0 1.8Z"/>' },
    hibernate: { color: '#a78bfa', svg: '<path d="M20.4 14.4A8.6 8.6 0 0 1 9.6 3.6 8.8 8.8 0 1 0 20.4 14.4Z"/><path d="M16.5 4.5v3M15 6h3"/>' },
    shutdown: { color: '#fb7185', svg: '<path d="M12 3v8"/><path d="M6.1 6.7a8 8 0 1 0 11.8 0"/>' },
    screenoff: { color: '#60a5fa', svg: '<rect x="3.5" y="4.5" width="17" height="12" rx="2"/><path d="M8 20h8M12 16.5V20M8 8l8 5"/>' },
    lock: { color: '#fb7185', svg: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3M12 14v3"/>' },
    browser: { color: '#38bdf8', svg: '<circle cx="12" cy="12" r="9"/><path d="M3.5 9h17M3.5 15h17M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/>' },
    folder: { color: '#fbbf24', svg: '<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/><path d="M3 10h18"/>' },
    mute: { color: '#f59e0b', svg: '<path d="M4 10v4h4l5 4V6l-5 4Z"/><path d="m17 9 4 6m0-6-4 6"/>' },
    launch: { color: '#cbd5e1', svg: '<path d="M13 5h6v6M19 5l-9 9"/><path d="M18 13v5a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>' },
  };
  window.phoneboxShortcutIcon = (shortcut) => {
    const name = `${shortcut?.label || ''} ${shortcut?.id || ''}`.toLowerCase();
    if (/\b(after\s*effects|aftereffects|ae)\b/.test(name)) return icons.aftereffects;
    if (/\bpremiere\b/.test(name)) return icons.premiere;
    if (/\bspotify\b/.test(name)) return icons.spotify;
    if (/\bdiscord\b/.test(name)) return icons.discord;
    if (/\blark\b/.test(name)) return icons.lark;
    if (/\b(terminal|powershell|command prompt|console|shell)\b/.test(name)) return icons.terminal;
    if (/\b(hibernate|sleep)\b/.test(name)) return icons.hibernate;
    if (/\b(shutdown|power off|poweroff)\b/.test(name)) return icons.shutdown;
    if (/\b(screen off|monitor off|display off)\b/.test(name)) return icons.screenoff;
    if (/\block\b/.test(name)) return icons.lock;
    if (/\b(settings|preferences)\b/.test(name)) return icons.settings;
    if (/\b(browser|chrome|edge|firefox)\b/.test(name)) return icons.browser;
    if (/\b(files|explorer|folder)\b/.test(name)) return icons.folder;
    if (/\b(mute|speaker)\b/.test(name)) return icons.mute;
    return icons.launch;
  };
})();
