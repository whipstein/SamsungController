// Presentation only: sticky geometry and per-browser-tab section/scroll memory.
// No focus changes, DOM reparenting, TV reads, or server-side preferences.
window.samsungMenuToolbar = (() => {
    const attached = new WeakMap();
    const sections = new Set(['expert', 'white2', 'white20', 'color', 'sound', 'system']);
    const storageKey = 'samsung-menu-view-v1';
    const positions = Object.create(null);
    let lastSection = 'expert', loaded = false, persistFrame;
    function load() {
        if (loaded) return;
        loaded = true;
        try {
            const saved = JSON.parse(window.sessionStorage.getItem(storageKey));
            if (sections.has(saved?.section)) lastSection = saved.section;
            for (const section of sections) {
                const position = saved?.positions?.[section];
                if (Number.isFinite(position) && position >= 0 && position <= 10000000) positions[section] = position;
            }
        } catch { /* Blocked/corrupt storage falls back to memory for this page session. */ }
    }
    function persist() {
        if (persistFrame) window.cancelAnimationFrame(persistFrame);
        persistFrame = undefined;
        try { window.sessionStorage.setItem(storageKey, JSON.stringify({ section: lastSection, positions })); } catch { }
    }
    function schedulePersist() { persistFrame ??= window.requestAnimationFrame(persist); }
    function offset(state) { return (state.header?.getBoundingClientRect().height ?? 0) + state.toolbar.getBoundingClientRect().height + 12; }
    function capture(state) {
        if (!state.section || state.restoring || state.content?.isConnected === false ||
            state.toolbar.dataset.menuSection !== state.section) return;
        positions[state.section] = Math.max(0, offset(state) - state.content.getBoundingClientRect().top);
        schedulePersist();
    }
    function attach(toolbar, content, section) {
        if (!toolbar || !content) return null;
        load();
        if (attached.has(toolbar)) return lastSection;
        const header = document.querySelector('.main-stage > .topbar');
        const update = () => toolbar.style.setProperty('--menu-sticky-offset', `${header?.getBoundingClientRect().height ?? 0}px`);
        const observer = new ResizeObserver(update);
        const state = { observer, update, header, toolbar, content, section: null, restoring: false, restoreFrame: null };
        state.scroll = () => capture(state);
        if (header) observer.observe(header);
        window.addEventListener('resize', update);
        window.addEventListener('scroll', state.scroll, { passive: true });
        window.addEventListener('pagehide', persist);
        attached.set(toolbar, state);
        update();
        return sections.has(lastSection) ? lastSection : section;
    }
    function savePosition(toolbar) {
        const state = attached.get(toolbar);
        if (!state) return;
        capture(state);
        // Ignore scroll events caused by Blazor replacing a long section with a short one.
        state.section = null;
        persist();
    }
    function restorePosition(toolbar, content, section) {
        const state = attached.get(toolbar);
        if (!content || !state || !sections.has(section)) return;
        if (state.restoreFrame) window.cancelAnimationFrame(state.restoreFrame);
        state.content = content;
        state.section = section;
        state.restoring = true;
        lastSection = section;
        const start = window.scrollY + content.getBoundingClientRect().top - offset(state);
        window.scrollTo({ top: Math.max(0, start + (positions[section] ?? 0)), behavior: 'instant' });
        // Programmatic scroll/height clamping must not replace the remembered position.
        state.restoreFrame = window.requestAnimationFrame(() => {
            state.restoreFrame = window.requestAnimationFrame(() => { state.restoring = false; state.restoreFrame = null; });
        });
        persist();
    }
    function detach(toolbar) {
        const state = attached.get(toolbar);
        if (!state) return;
        capture(state);
        persist();
        if (state.restoreFrame) window.cancelAnimationFrame(state.restoreFrame);
        state.observer.disconnect();
        window.removeEventListener('resize', state.update);
        window.removeEventListener('scroll', state.scroll);
        window.removeEventListener('pagehide', persist);
        attached.delete(toolbar);
    }
    return { attach, detach, savePosition, restorePosition };
})();
