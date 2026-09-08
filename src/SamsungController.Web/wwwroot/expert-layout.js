// Native drag/drop feedback stays local; Blazor alone reorders the rendered DOM.
// Only the handle is draggable, so sliders, switches and text inputs keep their behavior.
window.samsungExpertLayout = (() => {
    const bindings = new WeakMap();
    function attach(root, receiver) {
        if (!root || bindings.has(root)) return;
        let source = null, marked = null, pending = false;
        const locked = () => pending || root.dataset.layoutLocked === "true";
        const groupAt = target => {
            const group = target?.closest?.("[data-expert-group]");
            return group && root.contains(group) ? group : null;
        };
        function clearMark() {
            marked?.classList.remove("layout-drop-before", "layout-drop-after");
            marked = null;
        }
        function clearDrag() {
            clearMark();
            source?.classList.remove("layout-dragging");
            source = null;
        }
        function placement(event, target) {
            const grid = root.querySelector(".direct-expert-grid");
            const horizontal = getComputedStyle(grid).gridTemplateColumns.trim().split(/\s+/).length > 1;
            root.dataset.layoutAxis = horizontal ? "horizontal" : "vertical";
            const rect = target.getBoundingClientRect();
            return horizontal ? event.clientX >= rect.left + rect.width / 2 : event.clientY >= rect.top + rect.height / 2;
        }
        function dragStart(event) {
            const handle = event.target?.closest?.("[data-layout-handle]");
            if (!handle || !root.contains(handle)) return;
            if (locked() || handle.disabled) { event.preventDefault(); return; }
            source = groupAt(handle);
            if (!source || !event.dataTransfer) { clearDrag(); event.preventDefault(); return; }
            event.dataTransfer.effectAllowed = "move";
            // Required by Firefox/Safari; incoming external payloads are never trusted.
            event.dataTransfer.setData("text/plain", source.dataset.expertGroup);
            event.dataTransfer.setDragImage?.(source, 16, 16);
            source.classList.add("layout-dragging");
        }
        function dragOver(event) {
            if (!source || locked()) { clearMark(); return; }
            const target = groupAt(event.target);
            clearMark();
            if (!target || target === source) return;
            event.preventDefault();
            if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
            target.classList.add(placement(event, target) ? "layout-drop-after" : "layout-drop-before");
            marked = target;
        }
        async function drop(event) {
            const target = groupAt(event.target);
            if (!source || locked() || !target || target === source) { clearDrag(); return; }
            event.preventDefault();
            const id = source.dataset.expertGroup;
            const targetId = target.dataset.expertGroup;
            const after = placement(event, target);
            pending = true;
            clearDrag();
            try { await receiver.invokeMethodAsync("MoveExpertGroupAsync", id, targetId, after); }
            catch (error) {
                // No optimistic DOM move to undo. The app's reconnect banner handles
                // circuit loss; ordinary save errors are shown by the .NET callback.
                console.warn("Expert layout was not saved; retry after reconnecting.", error);
            }
            finally { pending = false; }
        }
        function dragLeave(event) { if (!root.contains(event.relatedTarget)) clearMark(); }
        const listeners = { dragstart: dragStart, dragover: dragOver, drop, dragend: clearDrag, dragleave: dragLeave };
        for (const [type, listener] of Object.entries(listeners)) root.addEventListener(type, listener);
        bindings.set(root, () => {
            clearDrag();
            for (const [type, listener] of Object.entries(listeners)) root.removeEventListener(type, listener);
        });
    }
    function detach(root) { bindings.get(root)?.(); bindings.delete(root); }
    return { attach, detach };
})();
