// Keep Menu's controls directly below the variable-height connection header.
// No DOM reparenting, focus changes, or TV operations.
window.samsungMenuToolbar = (() => {
    const attached = new WeakMap();
    function attach(toolbar) {
        if (!toolbar || attached.has(toolbar)) return;
        const header = document.querySelector('.main-stage > .topbar');
        const update = () => toolbar.style.setProperty('--menu-sticky-offset', `${header?.getBoundingClientRect().height ?? 0}px`);
        const observer = new ResizeObserver(update);
        if (header) observer.observe(header);
        window.addEventListener('resize', update);
        attached.set(toolbar, { observer, update, header });
        update();
    }
    function detach(toolbar) {
        const state = attached.get(toolbar);
        if (!state) return;
        state.observer.disconnect();
        window.removeEventListener('resize', state.update);
        attached.delete(toolbar);
    }
    function scrollToContent(toolbar, content) {
        const state = attached.get(toolbar);
        if (!content || !state) return;
        const offset = (state.header?.getBoundingClientRect().height ?? 0) + toolbar.getBoundingClientRect().height + 12;
        window.scrollTo({ top: Math.max(0, window.scrollY + content.getBoundingClientRect().top - offset), behavior: 'instant' });
    }
    return { attach, detach, scrollToContent };
})();
