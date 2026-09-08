const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../../src/SamsungController.Web/wwwroot/menu-toolbar.js'), 'utf8');
function setup({ hasHeader = true, storage = new Map(), blockedStorage = false } = {}) {
    let height = 160, callback, disconnected = 0, observers = 0, frameId = 0, maxScroll = Infinity;
    const style = {}, listeners = new Map(), scrolls = [], frames = new Map();
    const header = { getBoundingClientRect: () => ({ height }) };
    const toolbar = { dataset: { menuSection: 'expert' }, style: { setProperty: (key, value) => style[key] = value }, getBoundingClientRect: () => ({ height: 140 }) };
    const content = { isConnected: true, getBoundingClientRect: () => ({ top: 500 - window.scrollY }) };
    const window = {
        scrollY: 1000,
        sessionStorage: { getItem: key => { if (blockedStorage) throw Error('denied'); return storage.get(key) ?? null; }, setItem: (key, value) => { if (blockedStorage) throw Error('denied'); storage.set(key, value); } },
        addEventListener: (event, fn) => listeners.set(event, fn), removeEventListener: event => listeners.delete(event),
        scrollTo: value => { scrolls.push(value); window.scrollY = Math.min(value.top, maxScroll); listeners.get('scroll')?.(); },
        requestAnimationFrame: fn => { frames.set(++frameId, fn); return frameId; }, cancelAnimationFrame: id => frames.delete(id)
    };
    const context = { window, document: { querySelector: () => hasHeader ? header : null }, ResizeObserver: class { constructor(fn) { callback = fn; observers++; } observe() {} disconnect() { disconnected++; } } };
    vm.runInNewContext(script, context);
    const api = window.samsungMenuToolbar;
    const flush = () => { while (frames.size) { const batch = [...frames.values()]; frames.clear(); batch.forEach(fn => fn()); } };
    return {
        api, toolbar, content, style, listeners, scrolls, storage, flush, window,
        attach: () => api.attach(toolbar, content, toolbar.dataset.menuSection),
        restore: section => { toolbar.dataset.menuSection = section; api.restorePosition(toolbar, content, section); flush(); },
        userScroll: value => { window.scrollY = value; listeners.get('scroll')?.(); flush(); },
        resize: value => { height = value; callback(); },
        clamp: value => maxScroll = value,
        counts: () => ({ disconnected, observers })
    };
}
test('sticky offset follows connection header wrapping, warnings and resizing', () => {
    const f = setup(); f.attach();
    assert.equal(f.style['--menu-sticky-offset'], '160px');
    f.resize(225); f.listeners.get('resize')();
    assert.equal(f.style['--menu-sticky-offset'], '225px');
});
test('first visits scroll below both pinned bars without focusing or selecting headings', () => {
    const f = setup(); f.attach(); f.restore('expert');
    assert.equal(f.scrolls[0].top, 188);
    assert.equal(f.scrolls[0].behavior, 'instant');
    f.api.savePosition(f.toolbar); f.resize(600); f.restore('sound');
    assert.equal(f.scrolls[1].top, 0);
});
test('each section restores its own position, including long to short and back', () => {
    const f = setup(); f.attach(); f.restore('white20'); f.userScroll(4500);
    f.api.savePosition(f.toolbar); f.clamp(800); f.restore('expert'); f.userScroll(350);
    f.api.savePosition(f.toolbar); f.clamp(Infinity); f.restore('white20');
    assert.equal(f.window.scrollY, 4500);
    f.api.savePosition(f.toolbar); f.restore('expert');
    assert.equal(f.window.scrollY, 350);
});
test('DOM replacement scroll events cannot overwrite the outgoing section', () => {
    const f = setup(); f.attach(); f.restore('white20'); f.userScroll(3500);
    f.toolbar.dataset.menuSection = 'sound'; f.userScroll(500);
    f.restore('sound'); f.api.savePosition(f.toolbar); f.restore('white20');
    assert.equal(f.window.scrollY, 3500);
});
test('remembered offsets adjust for a changed sticky header height', () => {
    const f = setup(); f.attach(); f.restore('white20'); f.userScroll(2200);
    f.api.savePosition(f.toolbar); f.restore('color'); f.api.savePosition(f.toolbar);
    f.resize(210); f.restore('white20');
    assert.equal(f.window.scrollY, 2150);
});
test('navigation away and back retains active section and its offset', () => {
    const f = setup(); f.attach(); f.restore('color'); f.userScroll(1400);
    f.content.isConnected = false; f.api.detach(f.toolbar);
    f.content.isConnected = true; f.window.scrollY = 0;
    assert.equal(f.attach(), 'color'); f.restore('color');
    assert.equal(f.window.scrollY, 1400);
});
test('browser-tab storage restores section and offset after a page reload', () => {
    const storage = new Map(), first = setup({ storage });
    first.attach(); first.restore('white20'); first.userScroll(2800); first.listeners.get('pagehide')();
    const next = setup({ storage }); assert.equal(next.attach(), 'white20'); next.restore('white20');
    assert.equal(next.window.scrollY, 2800);
    const separateTab = setup(); assert.equal(separateTab.attach(), 'expert'); separateTab.restore('white20');
    assert.equal(separateTab.window.scrollY, 188);
});
test('blocked storage still remembers positions in memory', () => {
    const f = setup({ blockedStorage: true }); f.attach(); f.restore('system'); f.userScroll(750);
    f.api.savePosition(f.toolbar); f.restore('sound'); f.api.savePosition(f.toolbar); f.restore('system');
    assert.equal(f.window.scrollY, 750);
});
test('corrupt storage and unknown sections cannot break navigation', () => {
    for (const saved of ['not json', '{"section":"invalid","positions":{"expert":-1,"white20":"500","sound":1e99}}']) {
        const f = setup({ storage: new Map([['samsung-menu-view-v1', saved]]) });
        assert.equal(f.attach(), 'expert'); f.restore('white20');
        assert.equal(f.window.scrollY, 188);
        const count = f.scrolls.length;
        f.api.restorePosition(f.toolbar, f.content, 'invalid');
        assert.equal(f.scrolls.length, count);
    }
});
test('attachment is idempotent; disposal removes observers, events and pending frames', () => {
    const f = setup(); f.attach(); f.attach();
    assert.equal(f.counts().observers, 1);
    f.restore('expert'); f.api.detach(f.toolbar); f.api.detach(f.toolbar);
    assert.equal(f.counts().disconnected, 1); assert.equal(f.listeners.size, 0);
    const count = f.scrolls.length; f.api.restorePosition(f.toolbar, f.content, 'expert');
    assert.equal(f.scrolls.length, count); f.attach(); assert.equal(f.counts().observers, 2);
});
test('missing header and missing roots are safe', () => {
    const f = setup({ hasHeader: false });
    assert.equal(f.api.attach(null, f.content, 'expert'), null);
    f.api.savePosition(null); f.api.detach(null); f.attach();
    assert.equal(f.style['--menu-sticky-offset'], '0px');
});
