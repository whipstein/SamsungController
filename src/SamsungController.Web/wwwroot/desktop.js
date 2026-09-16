// Only an explicit Quit event from this server instance may close this tab.
// Network failures and ordinary Blazor reconnects must never close it.
(() => {
    let stream, instance, stopped = false, checking = false;
    async function returnToApp() {
        if (checking) return;
        checking = true;
        const button = document.getElementById('desktop-return-to-app');
        const status = document.getElementById('desktop-return-status');
        button.disabled = true;
        status.textContent = 'Checking whether SamsungController has reopened…';
        const cancellation = new AbortController();
        const timeout = window.setTimeout(() => cancellation.abort(), 2000);
        try {
            const response = await window.fetch('/_app/status', { cache:'no-store', credentials:'omit', signal:cancellation.signal });
            const app = response.ok ? await response.json() : null;
            if (app?.product === 'SamsungController' && app.managed === true && /^[a-f0-9]{32}$/.test(app.instance)
                && app.instance !== instance) {
                window.location.reload();
                return;
            }
            status.textContent = 'The app is not ready yet. Reopen SamsungController, wait for it to start, then try again.';
        } catch {
            status.textContent = 'SamsungController is not running. Reopen the app, then click Return to app.';
        } finally {
            window.clearTimeout(timeout);
            button.disabled = false;
            checking = false;
        }
    }
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
                // A native popover/dialog could otherwise remain in the top layer
                // above the stopped page. No modal or server-backed controls remain.
                for (const popup of document.querySelectorAll('[popover]:popover-open')) popup.hidePopover();
                for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
                document.documentElement.classList.add('desktop-app-quit');
                const notice = document.getElementById('desktop-app-stopped');
                if (notice) notice.hidden = false;
                document.getElementById('desktop-return-to-app')?.addEventListener('click', returnToApp);
                // Release this tab's circuit even if the browser refuses to close it.
                // Otherwise its live connection can hold up graceful server shutdown.
                try { window.Blazor?.disconnect?.(); } catch { /* The stopped page does not depend on Blazor. */ }
                // Never enumerate browser tabs or close the browser process.
                // Browsers can silently refuse this. Do not offer a retry button
                // that repeats the same prohibited operation or trap focus in a modal.
                try { window.close(); } catch { /* The non-modal stopped page stays usable. */ }
            });
        }
    };
    window.addEventListener('pagehide', () => { stream?.close(); stream = undefined; });
    window.addEventListener('pageshow', () => { if (instance && !stream && !stopped) window.samsungDesktop.watch(instance); });
})();
