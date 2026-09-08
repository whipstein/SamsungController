// Pure event-handler tests with a small DOM double; no browser or TV connection.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const script = fs.readFileSync(path.join(__dirname, "../../src/SamsungController.Web/wwwroot/expert-layout.js"), "utf8");

function setup(columns = "350px 350px", invoke) {
    const calls = [], warnings = [], listeners = new Map();
    const root = {
        dataset: { layoutLocked: "false" },
        contains: node => node?.root === root,
        querySelector: () => ({}),
        addEventListener: (name, listener) => listeners.set(name, listener),
        removeEventListener: name => listeners.delete(name)
    };
    function group(id) {
        const classes = new Set();
        const element = {
            root, dataset: { expertGroup: id },
            classList: { add: name => classes.add(name), remove: (...names) => names.forEach(name => classes.delete(name)) },
            getBoundingClientRect: () => ({ left: 100, top: 200, width: 300, height: 400 }),
            closest: selector => selector === "[data-expert-group]" ? element : null,
            classes
        };
        const handle = { root, disabled: false, closest: selector => selector === "[data-layout-handle]" ? handle : element };
        const slider = { root, closest: selector => selector === "[data-expert-group]" ? element : null };
        return { element, handle, slider };
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
            dataTransfer: { setData(type, data) { this.payload = [type, data]; }, setDragImage() {} }
        };
    }
    const send = (name, data = event(source.handle)) => listeners.get(name)?.(data);
    return { root, source, target, calls, warnings, listeners, api, receiver, event, send };
}

test("only a drag handle starts a move; sliders and external payloads do not", async () => {
    const f = setup();
    f.send("dragstart", f.event(f.source.slider));
    const over = f.event(f.target.element);
    f.send("dragover", over);
    assert.equal(over.prevented, false);
    await f.send("drop", over);
    assert.equal(f.calls.length, 0);
    const start = f.event(f.source.handle);
    f.send("dragstart", start);
    assert.equal(start.dataTransfer.effectAllowed, "move");
    assert.deepEqual(start.dataTransfer.payload, ["text/plain", "contrastControl/contrast"]);
    assert.equal(f.source.element.classes.has("layout-dragging"), true);
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
    assert.deepEqual(f.calls, [["MoveExpertGroupAsync", "contrastControl/contrast", "gammaModeControl/gammaMode", after]]);
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
    const locked = f.event(f.source.handle);
    f.send("dragstart", locked);
    assert.equal(locked.prevented, true);
    f.root.dataset.layoutLocked = "false";
    f.send("dragstart");
    const saving = f.send("drop", f.event(f.target.element));
    const again = f.event(f.source.handle);
    f.send("dragstart", again);
    assert.equal(again.prevented, true);
    assert.equal(f.calls.length, 1);
    finish();
    await saving;
    const allowed = f.event(f.source.handle);
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

test("reattach is idempotent, detach removes handlers, and save rejection leaves no drag state", async () => {
    const f = setup("350px", () => Promise.reject(new Error("simulated disconnected circuit")));
    f.api.attach(f.root, f.receiver);
    assert.equal(f.listeners.size, 5);
    f.send("dragstart");
    await f.send("drop", f.event(f.target.element));
    assert.equal(f.calls.length, 1);
    assert.equal(f.warnings.length, 1);
    assert.equal(f.source.element.classes.size, 0);
    f.api.detach(f.root);
    assert.equal(f.listeners.size, 0);
    f.api.attach(f.root, f.receiver);
    assert.equal(f.listeners.size, 5);
});
