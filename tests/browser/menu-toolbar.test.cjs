const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../../src/SamsungController.Web/wwwroot/menu-toolbar.js'), 'utf8');
function setup(hasHeader = true) {
    let height = 160, callback, disconnected = 0, observers = 0;
    const style = {}, listeners = new Map(), scrolls = [];
    const header = { getBoundingClientRect: () => ({ height }) };
    const toolbar = { style: { setProperty: (key, value) => style[key] = value }, getBoundingClientRect: () => ({ height: 140 }) };
    const content = { getBoundingClientRect: () => ({ top: -500 }) };
    const window = { scrollY: 1000, addEventListener: (event, fn) => listeners.set(event, fn), removeEventListener: event => listeners.delete(event), scrollTo: value => scrolls.push(value) };
    const context = { window, document: { querySelector: () => hasHeader ? header : null }, ResizeObserver: class { constructor(fn) { callback = fn; observers++; } observe() {} disconnect() { disconnected++; } } };
    vm.runInNewContext(script, context);
    return { api: window.samsungMenuToolbar, toolbar, content, style, listeners, scrolls, resize: value => { height = value; callback(); }, counts: () => ({ disconnected, observers }) };
}
test('sticky offset follows connection header wrapping, warnings and resizing', () => {
    const f = setup();
    f.api.attach(f.toolbar);
    assert.equal(f.style['--menu-sticky-offset'], '160px');
    f.resize(225);
    assert.equal(f.style['--menu-sticky-offset'], '225px');
    f.listeners.get('resize')();
    assert.equal(f.style['--menu-sticky-offset'], '225px');
});
test('switching sections scrolls below both pinned bars without focusing/selecting a heading', () => {
    const f = setup();
    f.api.attach(f.toolbar);
    f.api.scrollToContent(f.toolbar, f.content);
    assert.equal(f.scrolls[0].top, 188);
    assert.equal(f.scrolls[0].behavior, 'instant');
    f.resize(600);
    f.api.scrollToContent(f.toolbar, f.content);
    assert.equal(f.scrolls[1].top, 0);
});
test('attachment is idempotent and navigation away removes observers and listeners', () => {
    const f = setup();
    f.api.attach(f.toolbar); f.api.attach(f.toolbar);
    assert.equal(f.counts().observers, 1);
    f.api.detach(f.toolbar); f.api.detach(f.toolbar);
    assert.equal(f.counts().disconnected, 1);
    assert.equal(f.listeners.size, 0);
    f.api.scrollToContent(f.toolbar, f.content);
    assert.equal(f.scrolls.length, 0);
    f.api.attach(f.toolbar);
    assert.equal(f.counts().observers, 2);
});
test('missing header falls back to zero rather than hiding controls under a guessed offset', () => {
    const f = setup(false);
    f.api.attach(null); f.api.attach(f.toolbar);
    assert.equal(f.style['--menu-sticky-offset'], '0px');
});
