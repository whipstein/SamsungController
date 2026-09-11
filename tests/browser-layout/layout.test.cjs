// No packages, browser, TV, or personal data required: node --test tests/browser-layout/*.test.cjs
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const scripts = path.resolve(__dirname, '../../src/SamsungController.Web/wwwroot');

function harness(saved, blocked = false) {
    const values = new Map(Object.entries(saved ?? {})), session = new Map(), events = new Map(), frames = new Map();
    let frameId = 0;
    const root = { dataset: { theme: 'dark' } };
    const storage = map => ({ getItem(key) { if (blocked) throw Error('blocked'); return map.get(key) ?? null; },
        setItem(key, value) { if (blocked) throw Error('blocked'); map.set(key, value); } });
    const window = { localStorage: storage(values), sessionStorage: storage(session), innerHeight: 850, scrollY: 0,
        requestAnimationFrame(fn) { frames.set(++frameId, fn); return frameId; }, cancelAnimationFrame(id) { frames.delete(id); },
        addEventListener(name, fn) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(fn); },
        removeEventListener(name, fn) { events.get(name)?.delete(fn); },
        dispatchEvent(event) { for (const fn of events.get(event.type) ?? []) fn(event); },
        scrollTo({ top }) { window.scrollY = top; window.dispatchEvent({ type:'scroll' }); } };
    const style = () => { const values = new Map(); return { setProperty(k,v) { values.set(k,v); }, getPropertyValue(k) { return values.get(k); } }; };
    const content = { isConnected:true, getBoundingClientRect:() => ({top:220-window.scrollY}) };
    const header = { getBoundingClientRect:() => ({height:70}) };
    const toolbar = { dataset:{menuSection:'white20'}, style:style(), getBoundingClientRect:() => ({height:70}) };
    const rows = { style:style(), closest:()=>content, getBoundingClientRect:() => ({top:330-window.scrollY}) };
    const elements = { '[data-grid-section="white20"] .direct-indexed-rows':rows, '.main-stage > .topbar':header, '.direct-menu-toolbar':toolbar };
    const document = { documentElement:root, querySelector:key=>elements[key] ?? null };
    const context = vm.createContext({ window, document, CustomEvent:class { constructor(type) { this.type=type; } }, ResizeObserver:class { observe(){} disconnect(){} } });
    const run = file => vm.runInContext(readFileSync(path.join(scripts,file), 'utf8'), context);
    function flush() { for (let i=0;frames.size && i<10;i++) { const work=[...frames.values()];frames.clear();work.forEach(fn=>fn()); } assert.equal(frames.size,0); }
    return { window, document, values, session, events, rows, content, toolbar, run, flush, root };
}

test('Standard is the default; storage is opt-in and independent of theme', () => {
    const h=harness();h.run('layout.js');h.flush();
    assert.equal(h.root.dataset.layout,'standard');assert.equal(h.values.size,0);
    h.window.samsungLayout.toggle();h.flush();
    assert.equal(h.root.dataset.layout,'compact');assert.equal(h.values.get('samsungController.layout'),'compact');
    assert.equal(h.root.dataset.theme,'dark');
    h.window.samsungLayout.toggle();h.flush();assert.equal(h.root.dataset.layout,'standard');
});

test('Restores compact before rendering and rejects invalid persisted values', () => {
    for (const [saved, expected] of [['compact','compact'],['bad','standard']]) {
        const h=harness({'samsungController.layout':saved});h.run('layout.js');assert.equal(h.root.dataset.layout,expected);h.flush();
    }
});

test('Blocked storage remains usable without changing theme', () => {
    const h=harness({},true);h.run('layout.js');h.window.samsungLayout.toggle();h.flush();
    assert.equal(h.root.dataset.layout,'compact');assert.equal(h.root.dataset.theme,'dark');
});

test('Cross-tab layout changes propagate; unrelated theme events are ignored', () => {
    const h=harness();h.run('layout.js');
    h.values.set('samsungController.layout','compact');
    h.window.dispatchEvent({type:'storage',key:'samsungController.theme'});assert.equal(h.root.dataset.layout,'standard');
    h.window.dispatchEvent({type:'storage',key:'samsungController.layout'});assert.equal(h.root.dataset.layout,'compact');
    h.values.clear();h.window.dispatchEvent({type:'storage',key:null});assert.equal(h.root.dataset.layout,'standard');h.flush();
});

test('Fits 20 rows with a safe minimum; height is independent of scroll', () => {
    const h=harness({'samsungController.layout':'compact'});h.window.scrollY=68;h.run('layout.js');h.flush();
    assert.equal(h.rows.style.getPropertyValue('--compact-row-height'),'28px');
    h.window.scrollY=500;h.window.dispatchEvent({type:'resize'});h.flush();
    assert.equal(h.rows.style.getPropertyValue('--compact-row-height'),'28px');
    h.window.innerHeight=500;h.window.dispatchEvent({type:'resize'});h.flush();
    assert.equal(h.rows.style.getPropertyValue('--compact-row-height'),'22px');
    h.window.innerHeight=1500;h.window.dispatchEvent({type:'samsung-menu-view-restored'});h.flush();
    assert.equal(h.rows.style.getPropertyValue('--compact-row-height'),'30px');
});

test('Resize accounts for content that cannot scroll fully below the sticky header', () => {
    const h=harness({'samsungController.layout':'compact'});h.run('layout.js');h.flush();
    assert.equal(h.rows.style.getPropertyValue('--compact-row-height'),'25px');
    assert(330 + 20 * 25 <= h.window.innerHeight);
});

test('Menu scroll positions are independent per layout and retain selected section', () => {
    const h=harness();h.run('layout.js');h.run('menu-toolbar.js');
    const api=h.window.samsungMenuToolbar;
    assert.equal(api.attach(h.toolbar,h.content,'white20'),'expert');
    api.restorePosition(h.toolbar,h.content,'white20');h.flush();
    h.window.scrollTo({top:1068});h.flush(); // 1000px into Standard content.
    h.window.samsungLayout.toggle();h.flush();
    assert.equal(h.window.scrollY,68);assert.equal(h.toolbar.dataset.menuSection,'white20');
    h.window.scrollTo({top:168});h.flush(); // 100px into Compact content.
    h.window.samsungLayout.toggle();h.flush();assert.equal(h.window.scrollY,1068);
    h.window.samsungLayout.toggle();h.flush();assert.equal(h.window.scrollY,168);
    assert.equal(JSON.parse(h.session.get('samsung-menu-view-v1')).positions.white20,1000);
    assert.equal(JSON.parse(h.session.get('samsung-menu-view-compact-v1')).positions.white20,100);
    api.detach(h.toolbar);h.flush();
    assert.equal(h.events.get('samsung-layout-changing').size,0);
    assert.equal(h.events.get('samsung-layout-changed').size,0);
});

test('Bad saved scroll values are ignored and pending animation is cleaned up on detach', () => {
    const h=harness();h.session.set('samsung-menu-view-v1',JSON.stringify({section:'invalid',positions:{white20:-5,expert:9e12}}));
    h.run('layout.js');h.run('menu-toolbar.js');const api=h.window.samsungMenuToolbar;
    assert.equal(api.attach(h.toolbar,h.content,'white20'),'expert');
    api.restorePosition(h.toolbar,h.content,'white20');assert.equal(h.window.scrollY,68);
    api.detach(h.toolbar);h.flush();assert.equal(h.events.get('scroll').size,0);
});
