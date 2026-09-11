// Browser presentation only. No TV commands, endpoint/profile access, or theme changes.
(() => {
    'use strict';
    const root = document.documentElement;
    const key = 'samsungController.layout';
    const normalize = value => value === 'compact' ? 'compact' : 'standard';
    let frame;
    function fitRows(attempt = 0) {
        frame = undefined;
        if (root.dataset.layout !== 'compact') return;
        const rows = document.querySelector('[data-grid-section="white20"] .direct-indexed-rows');
        if (!rows) return;
        const content = rows.closest('.direct-menu-content');
        const header = document.querySelector('.main-stage > .topbar');
        const toolbar = document.querySelector('.direct-menu-toolbar');
        // Measure from the section heading at its normal restored position, not
        // from the current scroll offset. Updates must not move +/- under the pointer.
        const restoredOverhead = (header?.getBoundingClientRect().height ?? 0)
            + (toolbar?.getBoundingClientRect().height ?? 0)
            + rows.getBoundingClientRect().top - (content?.getBoundingClientRect().top ?? 0) + 26;
        // A taller window can clamp scroll to zero, leaving the main navigation
        // and Saved states visible above the content. Include that actual space.
        const overhead = Math.max(restoredOverhead, rows.getBoundingClientRect().top + 14);
        const height = Math.max(22, Math.min(30, Math.floor((window.innerHeight - overhead) / 20)));
        const value = `${height}px`;
        if (rows.style.getPropertyValue('--compact-row-height') !== value) {
            rows.style.setProperty('--compact-row-height', value);
            if (attempt < 2) frame = window.requestAnimationFrame(() => fitRows(attempt + 1));
        }
    }
    function scheduleFit() { if (frame === undefined) frame = window.requestAnimationFrame(() => fitRows()); }
    function apply(value, persist) {
        const layout = normalize(value), previous = normalize(root.dataset.layout);
        if (layout !== previous) window.dispatchEvent(new CustomEvent('samsung-layout-changing'));
        root.dataset.layout = layout;
        if (persist) { try { window.localStorage.setItem(key, layout); } catch { /* Page-local mode still works. */ } }
        if (layout !== previous) window.dispatchEvent(new CustomEvent('samsung-layout-changed'));
        scheduleFit();
        return layout;
    }
    let saved;
    try { saved = window.localStorage.getItem(key); } catch { }
    apply(saved, false); // Runs before CSS so a remembered compact page does not flash large cards.
    window.samsungLayout = { get: () => normalize(root.dataset.layout), set: value => apply(value, true),
        toggle: () => apply(root.dataset.layout === 'compact' ? 'standard' : 'compact', true) };
    window.addEventListener('resize', scheduleFit);
    window.addEventListener('samsung-menu-view-restored', scheduleFit);
    window.addEventListener('storage', event => { if (event.key === key || event.key === null) {
        let value; try { value = window.localStorage.getItem(key); } catch { }
        apply(value, false);
    } });
})();
