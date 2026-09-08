// Pure event-handler tests with a small DOM double; no browser or TV connection.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const script = fs.readFileSync(path.join(__dirname, "../../src/SamsungController.Web/wwwroot/expert-layout.js"), "utf8");

function setup(columns = "350px 350px", invoke, section = "expert") {
    const calls = [], warnings = [], listeners = new Map();
    const root = {
        dataset: { layoutLocked: "false", layoutSection: section },
        contains: node => node?.root === root,
        querySelector: () => ({}),
        querySelectorAll: () => [source.element, target.element],
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    function group(id) {
        const classes = new Set();
        const element = {
            root, dataset: { expertGroup: id }, draggable: true,
            classList: { add: name => classes.add(name), remove: (...names) => names.forEach(name => classes.delete(name)) },
            getBoundingClientRect: () => ({ left: 100, top: 200, width: 300, height: 400 }),
            closest: selector => selector === "[data-expert-group]" ? element : null,
            classes
        };
        const child = (interactive = false) => {
            const node = { root, closest: selector => selector === "[data-expert-group]" ? element : interactive && selector.startsWith("input,") ? node : null };
            return node;
        };
        return { element, heading: child(), slider: child(true), child };
    }
    const source = group("contrastControl/contrast"), target = group("gammaModeControl/gammaMode");
    const receiver = { invokeMethodAsync: async (...args) => { calls.push(args); if (invoke) return await invoke(); return true; } };
    const context = { window: {}, getComputedStyle: () => ({ gridTemplateColumns: columns }), console: { warn: (...args) => warnings.push(args) } };
    vm.runInNewContext(script, context);
    const api = context.window.samsungExpertLayout;
    api.attach(root, receiver);
    function event(node, x = 110, y = 210) {
        return {
            target: node, clientX: x, clientY: y, relatedTarget: null, prevented: false,
            preventDefault() { this.prevented = true; },
            dataTransfer: { setData(type, data) { this.payload = [type, data]; }, setDragImage(...args) { this.image = args; } }
        };
    }
    const send = (name, data = event(source.element)) => listeners.get(name)?.(data);
    return { root, source, target, calls, warnings, listeners, api, receiver, event, send };
}

test("whole cards and headings drag the entire group; sliders and external payloads do not", async () => {
    const f = setup();
    f.send("dragstart", f.event(f.source.slider));
    const over = f.event(f.target.element);
    f.send("dragover", over);
    assert.equal(over.prevented, false);
    await f.send("drop", over);
    assert.equal(f.calls.length, 0);
    const start = f.event(f.source.heading);
    f.send("dragstart", start);
    assert.equal(start.dataTransfer.effectAllowed, "move");
    assert.deepEqual(start.dataTransfer.payload, ["text/plain", "contrastControl/contrast"]);
    assert.equal(start.dataTransfer.image[0], f.source.element);
    assert.deepEqual(start.dataTransfer.image.slice(1), [10, 10]);
    assert.equal(f.source.element.classes.has("layout-dragging"), true);
});

for (const section of ["sound", "system"]) test(`${section} moves keep the originating section even after a tab change`, async () => {
    const f = setup("350px", undefined, section);
    f.root.dataset.layoutSection = "expert";
    f.send("dragstart");
    await f.send("drop", f.event(f.target.element));
    assert.deepEqual(f.calls, [["MoveMenuGroupAsync", section, "contrastControl/contrast", "gammaModeControl/gammaMode", false]]);
    f.api.detach(f.root);
    assert.equal(f.listeners.size, 0);
});

test("a layout without a section identity never attaches drag handlers", () => {
    const f = setup("350px", undefined, null);
    assert.equal(f.listeners.size, 0);
    assert.equal(f.calls.length, 0);
});

for (const [columns, x, y, after, axis] of [
    ["350px 350px", 110, 590, false, "horizontal"],
    ["350px 350px", 390, 210, true, "horizontal"],
    ["350px", 390, 210, false, "vertical"],
    ["350px", 110, 590, true, "vertical"]
]) test(`drop ${after ? "after" : "before"} in ${axis} layout`, async () => {
    const f = setup(columns);
    f.send("dragstart");
    const event = f.event(f.target.slider, x, y);
    f.send("dragover", event);
    assert.equal(event.prevented, true);
    assert.equal(event.dataTransfer.dropEffect, "move");
    assert.equal(f.root.dataset.layoutAxis, axis);
    assert.equal(f.target.element.classes.has(after ? "layout-drop-after" : "layout-drop-before"), true);
    await f.send("drop", event);
    assert.deepEqual(f.calls, [["MoveMenuGroupAsync", "expert", "contrastControl/contrast", "gammaModeControl/gammaMode", after]]);
    assert.equal(f.source.element.classes.size, 0);
    assert.equal(f.target.element.classes.size, 0);
});

test("Escape/dragend, outside drops, self drops, and leaving the grid clean up without saving", async () => {
    for (const ending of ["dragend", "outside", "self"]) {
        const f = setup();
        f.send("dragstart");
        f.send("dragover", f.event(f.target.element));
        f.send("dragleave", f.event(f.target.element));
        assert.equal(f.target.element.classes.size, 0);
        if (ending === "dragend") f.send("dragend");
        else await f.send("drop", f.event(ending === "self" ? f.source.element : null));
        assert.equal(f.source.element.classes.size, 0);
        assert.equal(f.calls.length, 0);
    }
});

test("disabled layout and in-flight saves prevent overlapping moves", async () => {
    let finish;
    const wait = new Promise(resolve => { finish = resolve; });
    const f = setup("350px", () => wait);
    f.root.dataset.layoutLocked = "true";
    const locked = f.event(f.source.element);
    f.send("dragstart", locked);
    assert.equal(locked.prevented, true);
    f.root.dataset.layoutLocked = "false";
    f.send("dragstart");
    const saving = f.send("drop", f.event(f.target.element));
    const again = f.event(f.source.element);
    f.send("dragstart", again);
    assert.equal(again.prevented, true);
    assert.equal(f.calls.length, 1);
    finish();
    await saving;
    const allowed = f.event(f.source.element);
    f.send("dragstart", allowed);
    assert.equal(allowed.prevented, false);
});

test("a TV operation starting mid-drag prevents the drop", async () => {
    const f = setup();
    f.send("dragstart");
    f.root.dataset.layoutLocked = "true";
    await f.send("drop", f.event(f.target.element));
    assert.equal(f.calls.length, 0);
    assert.equal(f.source.element.classes.size, 0);
});

test("internal self-drops on an input never insert the drag payload as a value", async () => {
    const f = setup();
    f.send("dragstart");
    const self = f.event(f.source.slider);
    await f.send("drop", self);
    assert.equal(self.prevented, true);
    assert.equal(f.calls.length, 0);
    f.send("dragstart");
    f.root.dataset.layoutLocked = "true";
    const locked = f.event(f.target.slider);
    await f.send("drop", locked);
    assert.equal(locked.prevented, true);
    assert.equal(f.calls.length, 0);
});

test("reattach is idempotent, detach removes handlers, and save rejection leaves no drag state", async () => {
    const f = setup("350px", () => Promise.reject(new Error("simulated disconnected circuit")));
    f.api.attach(f.root, f.receiver);
    assert.equal(f.listeners.size, 9);
    f.send("dragstart");
    await f.send("drop", f.event(f.target.element));
    assert.equal(f.calls.length, 1);
    assert.equal(f.warnings.length, 1);
    assert.equal(f.source.element.classes.size, 0);
    f.api.detach(f.root);
    assert.equal(f.listeners.size, 0);
    f.api.attach(f.root, f.receiver);
    assert.equal(f.listeners.size, 9);
});

test("input gestures suppress ancestor dragging without preventing normal input events", async () => {
    const f = setup();
    const down = f.event(f.source.slider);
    f.send("pointerdown", down);
    assert.equal(down.prevented, false);
    assert.equal(f.source.element.draggable, false);
    // Native browsers can report the ancestor as dragstart.target, even if the
    // gesture began on a nested input. A render can also reset draggable.
    f.source.element.draggable = true;
    const drag = f.event(f.source.element);
    f.send("dragstart", drag);
    assert.equal(drag.prevented, true);
    await f.send("drop", f.event(f.target.element));
    assert.equal(f.calls.length, 0);
    f.send("pointerup", f.event(f.source.slider));
    assert.equal(f.source.element.draggable, true);
    f.send("pointerdown", f.event(f.source.heading));
    const wholeBox = f.event(f.source.element);
    f.send("dragstart", wholeBox);
    assert.equal(wholeBox.prevented, false);
    await f.send("drop", f.event(f.target.element));
    assert.equal(f.calls.length, 1);
});

test("pointer cancellation and detach restore the input's draggable ancestor", () => {
    const f = setup();
    f.send("pointerdown", f.event(f.source.slider));
    f.send("pointercancel");
    assert.equal(f.source.element.draggable, true);
    const canceledDrag = f.event(f.source.element);
    f.send("dragstart", canceledDrag);
    assert.equal(canceledDrag.prevented, true);
    f.send("pointerdown", f.event(f.source.slider));
    f.api.detach(f.root);
    assert.equal(f.source.element.draggable, true);
    assert.equal(f.listeners.size, 0);
});

test("moving to another card after an input gesture restores both cards", () => {
    const f = setup();
    f.send("pointerdown", f.event(f.source.slider));
    f.send("pointerdown", f.event(f.target.heading));
    assert.equal(f.source.element.draggable, true);
    assert.equal(f.target.element.draggable, true);
    const drag = f.event(f.target.element);
    f.send("dragstart", drag);
    assert.equal(drag.prevented, false);
});

for (const key of ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"]) test(`Alt+${key} moves only the focused whole box`, async () => {
    const f = setup();
    const backwards = key === "ArrowLeft" || key === "ArrowUp";
    const current = backwards ? f.target : f.source;
    const keyboard = node => Object.assign(f.event(node), { key, altKey: true });
    const input = keyboard(current.slider);
    await f.send("keydown", input);
    assert.equal(input.prevented, false);
    assert.equal(f.calls.length, 0);
    const card = keyboard(current.element);
    await f.send("keydown", card);
    assert.equal(card.prevented, true);
    assert.deepEqual(f.calls, [["MoveMenuGroupAsync", "expert", current.element.dataset.expertGroup,
        (backwards ? f.source : f.target).element.dataset.expertGroup, !backwards]]);
});

test("plain arrows, boundary shortcuts and locked layouts never move settings", async () => {
    const f = setup();
    await f.send("keydown", Object.assign(f.event(f.source.element), { key: "ArrowRight" }));
    await f.send("keydown", Object.assign(f.event(f.source.element), { key: "ArrowLeft", altKey: true }));
    f.root.dataset.layoutLocked = "true";
    await f.send("keydown", Object.assign(f.event(f.source.element), { key: "ArrowRight", altKey: true }));
    assert.equal(f.calls.length, 0);
});
