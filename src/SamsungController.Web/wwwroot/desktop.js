// Only an explicit Quit event from this server instance may close this tab.
// Network failures and ordinary Blazor reconnects must never close it.
(() => {
    let stream, instance, stopped = false;
    function closeTab() { try { window.close(); } catch { /* Browser policy: leave the stopped screen visible. */ } }
    window.samsungDesktop = {
        watch(id) {
            if (!/^[a-f0-9]{32}$/.test(id) || stopped || instance === id && stream) return;
            stream?.close();
            instance = id;
            const events = new window.EventSource('/_app/events?instance=' + encodeURIComponent(id));
            stream = events;
            events.addEventListener('quit', event => {
                if (stream !== events || event.data !== instance || stopped) return;
                stopped = true;
                events.close();
                document.documentElement.classList.add('desktop-app-quit');
                const notice = document.getElementById('desktop-app-stopped');
                if (notice && !notice.open) notice.showModal();
                document.getElementById('desktop-close-tab')?.addEventListener('click', closeTab);
                // Never enumerate browser tabs or close the browser process.
                closeTab();
            });
        }
    };
    window.addEventListener('pagehide', () => { stream?.close(); stream = undefined; });
    window.addEventListener('pageshow', () => { if (instance && !stream && !stopped) window.samsungDesktop.watch(instance); });
})();
