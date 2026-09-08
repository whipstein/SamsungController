// Native drag/drop feedback stays local; Blazor alone reorders the rendered DOM.
// The whole card/group is draggable, except gestures starting on interactive controls.
window.samsungExpertLayout = (() => {
    const bindings = new WeakMap();
    function attach(root, receiver) {
        if (!root || bindings.has(root)) return;
        let source = null, marked = null, pending = false, suppressed = null, interactiveOrigin = false;
        const locked = () => pending || root.dataset.layoutLocked === "true";
        const interactive = target => !!target?.closest?.("input, select, textarea, button, a, label, summary, [contenteditable]:not([contenteditable='false']), [role='button'], [role='switch'], [role='slider'], [role='textbox']");
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
        function restoreDraggable() {
            if (suppressed) suppressed.draggable = !locked();
            suppressed = null;
        }
        function pointerDown(event) {
            restoreDraggable();
            interactiveOrigin = interactive(event.target);
            const group = groupAt(event.target);
            if (!group) return;
            // Native dragstart can target the draggable ancestor instead of the
            // input that was pressed. Disable that ancestor before the gesture.
            group.draggable = !locked() && !interactiveOrigin;
            if (interactiveOrigin) suppressed = group;
        }
        function placement(event, target) {
            const grid = root.querySelector(".direct-expert-grid");
            const horizontal = getComputedStyle(grid).gridTemplateColumns.trim().split(/\s+/).length > 1;
            root.dataset.layoutAxis = horizontal ? "horizontal" : "vertical";
            const rect = target.getBoundingClientRect();
            return horizontal ? event.clientX >= rect.left + rect.width / 2 : event.clientY >= rect.top + rect.height / 2;
        }
        function dragStart(event) {
            const group = groupAt(event.target);
            if (!group) return;
            if (locked() || interactiveOrigin || interactive(event.target)) { clearDrag(); event.preventDefault(); return; }
            source = group;
            if (!source || !event.dataTransfer) { clearDrag(); event.preventDefault(); return; }
            event.dataTransfer.effectAllowed = "move";
            // Required by Firefox/Safari; incoming external payloads are never trusted.
            event.dataTransfer.setData("text/plain", source.dataset.expertGroup);
            const rect = source.getBoundingClientRect();
            event.dataTransfer.setDragImage?.(source, Math.max(0, Math.min(rect.width, event.clientX - rect.left)), Math.max(0, Math.min(rect.height, event.clientY - rect.top)));
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
            if (!source) return;
            // Never let our internal text payload get inserted into an input,
            // even for a canceled/self drop or a layout that just became locked.
            event.preventDefault();
            const target = groupAt(event.target);
            if (locked() || !target || target === source) { clearDrag(); return; }
            const id = source.dataset.expertGroup;
            const targetId = target.dataset.expertGroup;
            const after = placement(event, target);
            clearDrag();
            await saveMove(id, targetId, after);
        }
        async function saveMove(id, targetId, after) {
            pending = true;
            try { await receiver.invokeMethodAsync("MoveExpertGroupAsync", id, targetId, after); }
            catch (error) {
                // No optimistic DOM move to undo. The app's reconnect banner handles
                // circuit loss; ordinary save errors are shown by the .NET callback.
                console.warn("Expert layout was not saved; retry after reconnecting.", error);
            }
            finally { pending = false; }
        }
        async function keyDown(event) {
            const group = groupAt(event.target);
            if (!group || event.target !== group || locked() || !event.altKey || event.ctrlKey || event.metaKey) return;
            const step = { ArrowLeft: -1, ArrowUp: -1, ArrowRight: 1, ArrowDown: 1 }[event.key];
            if (!step) return;
            event.preventDefault();
            const groups = Array.from(root.querySelectorAll("[data-expert-group]"));
            const target = groups[groups.indexOf(group) + step];
            if (target) await saveMove(group.dataset.expertGroup, target.dataset.expertGroup, step > 0);
        }
        function dragLeave(event) { if (!root.contains(event.relatedTarget)) clearMark(); }
        const listeners = { pointerdown: pointerDown, pointerup: restoreDraggable, pointercancel: restoreDraggable,
            dragstart: dragStart, dragover: dragOver, drop, dragend: clearDrag, dragleave: dragLeave, keydown: keyDown };
        for (const [type, listener] of Object.entries(listeners)) root.addEventListener(type, listener);
        bindings.set(root, () => {
            clearDrag();
            restoreDraggable();
            for (const [type, listener] of Object.entries(listeners)) root.removeEventListener(type, listener);
        });
    }
    function detach(root) { bindings.get(root)?.(); bindings.delete(root); }
    return { attach, detach };
})();
