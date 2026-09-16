const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/SamsungController.Web/wwwroot/desktop.js'), 'utf8');
const instance = '0123456789abcdef0123456789abcdef';
function harness(denied = false) {
    const streams = [], pageEvents = {}, classes = new Set(), button = { addEventListener(name, fn) { this[name] = fn; } };
    const dialog = { open: false, showModal() { this.open = true; } };
    let closes = 0;
    const window = { close() { closes++; if (denied) throw Error('browser blocked close'); }, addEventListener(name, fn) { pageEvents[name] = fn; },
        EventSource: class { constructor(url) { this.url = url; this.events = {}; streams.push(this); } addEventListener(name, fn) { this.events[name] = fn; } close() { this.closed = true; } } };
    const document = { documentElement: { classList: { add: name => classes.add(name) } }, getElementById: id => id === 'desktop-app-stopped' ? dialog : button };
    vm.runInNewContext(source, { window, document, encodeURIComponent });
    return { window, streams, dialog, button, pageEvents, classes, get closes() { return closes; } };
}
test('only explicit matching quit closes this tab; network errors and other instances do not', () => {
    const h = harness(); h.window.samsungDesktop.watch(instance);
    const stream = h.streams[0];
    assert.equal(stream.url, '/_app/events?instance=' + instance);
    stream.events.error?.({}); assert.equal(h.closes, 0);
    stream.events.quit({data: 'other-server'}); assert.equal(h.closes, 0);
    stream.events.quit({data: instance}); assert.equal(h.closes, 1);
    assert(h.dialog.open); assert(stream.closed); assert(h.classes.has('desktop-app-quit'));
    stream.events.quit({data: instance}); assert.equal(h.closes, 1);
});
test('blocked tab close leaves a stopped message and a manual close button', () => {
    const h = harness(true); h.window.samsungDesktop.watch(instance);
    h.streams[0].events.quit({data: instance});
    assert(h.dialog.open); assert.equal(h.closes, 1);
    h.button.click(); assert.equal(h.closes, 2);
});
test('subscription is reused, pagehide cleans up, and back-forward cache restores it', () => {
    const h = harness(); h.window.samsungDesktop.watch('invalid'); assert.equal(h.streams.length, 0);
    h.window.samsungDesktop.watch(instance); h.window.samsungDesktop.watch(instance); assert.equal(h.streams.length, 1);
    h.pageEvents.pagehide(); assert(h.streams[0].closed);
    h.pageEvents.pageshow(); assert.equal(h.streams.length, 2);
    h.streams[0].events.quit({data: instance}); assert.equal(h.closes, 0);
    h.streams[1].events.quit({data: instance}); assert.equal(h.closes, 1);
    h.pageEvents.pagehide(); h.pageEvents.pageshow(); assert.equal(h.streams.length, 2);
});
