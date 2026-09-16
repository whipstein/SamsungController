const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/SamsungController.Web/wwwroot/desktop.js'), 'utf8');
const instance = '0123456789abcdef0123456789abcdef';
const reopenedInstance = 'fedcba9876543210fedcba9876543210';
function harness(denied = false) {
    const streams = [], pageEvents = {}, classes = new Set(), timers = new Map(), requests = [];
    const button = { disabled:false, addEventListener(name, fn) { this[name] = fn; } };
    const notice = { hidden:true }, status = { textContent:'' };
    const popup = { open:true, hidePopover() { this.open = false; } }, previousDialog = { open:true, close() { this.open = false; } };
    let closes = 0, reloads = 0, timerId = 0;
    let reply = async () => { throw Error('server has stopped'); };
    const window = { close() { closes++; if (denied) throw Error('browser blocked close'); }, addEventListener(name, fn) { pageEvents[name] = fn; },
        location: { reload() { reloads++; } },
        fetch(url, options) { requests.push({url, options}); return reply(url, options); },
        setTimeout(fn, delay) { assert.equal(delay, 2000); timers.set(++timerId, fn); return timerId; }, clearTimeout(id) { timers.delete(id); },
        EventSource: class { constructor(url) { this.url = url; this.events = {}; streams.push(this); } addEventListener(name, fn) { this.events[name] = fn; } close() { this.closed = true; } } };
    const document = { documentElement: { classList: { add: name => classes.add(name) } },
        querySelectorAll: selector => selector === 'dialog[open]' ? [previousDialog] : [popup],
        getElementById: id => ({'desktop-app-stopped':notice, 'desktop-return-to-app':button, 'desktop-return-status':status})[id] };
    vm.runInNewContext(source, { window, document, encodeURIComponent, AbortController });
    return { window, streams, notice, button, status, pageEvents, classes, timers, requests, popup, previousDialog,
        setReply(fn) { reply = fn; }, get closes() { return closes; }, get reloads() { return reloads; } };
}
test('only explicit matching quit closes this tab; network errors and other instances do not', () => {
    const h = harness(); h.window.samsungDesktop.watch(instance);
    const stream = h.streams[0];
    assert.equal(stream.url, '/_app/events?instance=' + instance);
    stream.events.error?.({}); assert.equal(h.closes, 0);
    stream.events.quit({data: 'other-server'}); assert.equal(h.closes, 0);
    stream.events.quit({data: instance}); assert.equal(h.closes, 1);
    assert(!h.notice.hidden); assert(stream.closed); assert(h.classes.has('desktop-app-quit'));
    assert(!h.popup.open); assert(!h.previousDialog.open); assert.equal(h.requests.length, 0);
    stream.events.quit({data: instance}); assert.equal(h.closes, 1);
});
for (const denied of [false, true]) test(`silently blocked or throwing close leaves a usable non-modal fallback (${denied})`, async () => {
    const h = harness(denied); h.window.samsungDesktop.watch(instance);
    h.streams[0].events.quit({data: instance});
    assert(!h.notice.hidden); assert.equal(h.closes, 1); assert(!h.button.disabled);
    await h.button.click();
    assert.match(h.status.textContent, /not running.*Reopen the app/);
    assert(!h.button.disabled); assert.equal(h.closes, 1); assert.equal(h.reloads, 0); assert.equal(h.timers.size, 0);
    assert.equal(h.requests[0].url, '/_app/status'); assert.equal(h.requests[0].options.cache, 'no-store');
    h.setReply(async () => ({ok:true, json:async () => ({product:'SamsungController', managed:true, instance:reopenedInstance})}));
    await h.button.click(); assert.equal(h.reloads, 1); assert(!h.button.disabled);
});
test('return requires a ready replacement app, not the quitting instance or an unrelated server', async () => {
    const h = harness(); h.window.samsungDesktop.watch(instance); h.streams[0].events.quit({data:instance});
    for (const response of [
        {ok:false},
        {ok:true, json:async () => ({product:'SamsungController', managed:true, instance})},
        {ok:true, json:async () => ({product:'OtherApp', managed:true, instance:reopenedInstance})},
        {ok:true, json:async () => ({product:'SamsungController', managed:false, instance:reopenedInstance})},
        {ok:true, json:async () => ({product:'SamsungController', managed:true, instance:'invalid'})},
        {ok:true, json:async () => {throw Error('bad JSON');}}
    ]) {
        h.setReply(async () => response); await h.button.click();
        assert.equal(h.reloads, 0); assert(!h.button.disabled); assert.match(h.status.textContent, /Reopen/);
    }
});
test('a stalled status check times out and permits another attempt without freezing', async () => {
    const h = harness(); h.window.samsungDesktop.watch(instance); h.streams[0].events.quit({data:instance});
    h.setReply((url, options) => new Promise((resolve, reject) => options.signal.addEventListener('abort', () => reject(Error('aborted')))));
    const checking = h.button.click(); assert(h.button.disabled);
    await h.button.click(); assert.equal(h.requests.length, 1);
    for (const timeout of h.timers.values()) timeout();
    await checking; assert(!h.button.disabled); assert.equal(h.timers.size, 0);
    assert.match(h.status.textContent, /not running/); assert.equal(h.reloads, 0);
});
test('fallback lives outside the server circuit, with no modal or misleading close button', () => {
    const root = path.join(__dirname, '../../src/SamsungController.Web');
    const page = fs.readFileSync(path.join(root, 'Components/Shared/DesktopStoppedPage.razor'), 'utf8');
    const app = fs.readFileSync(path.join(root, 'Components/App.razor'), 'utf8');
    const css = fs.readFileSync(path.join(root, 'wwwroot/direct.css'), 'utf8');
    assert.match(page, /<main[^>]*id="desktop-app-stopped"[^>]*hidden/);
    assert.match(page, /id="desktop-return-to-app">Return to app/);
    assert.doesNotMatch(page, /<dialog|@onclick|@rendermode|Close this tab/);
    assert.match(app, /<Routes @rendermode="InteractiveServer" \/>\s*<DesktopStoppedPage \/>/);
    assert.doesNotMatch(source, /showModal|desktop-close-tab/);
    assert.match(css, /\.desktop-app-quit body > :not\(#desktop-app-stopped\)/);
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
