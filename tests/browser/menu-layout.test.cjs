const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../../src/SamsungController.Web');
const css = fs.readFileSync(path.join(root, 'wwwroot/direct.css'), 'utf8');
function rule(selector) {
    const start = css.indexOf(selector + ' {');
    assert.notEqual(start, -1, `Missing ${selector}`);
    return css.slice(start, css.indexOf('}', start));
}
// Layout contracts only; these do not replace rendered browser verification.
test('pinned actions override the global full-width primary button and do not stack Stop', () => {
    const actions = rule('.direct-menu-toolbar .direct-actions');
    assert.match(actions, /flex-wrap:nowrap/);
    assert.match(actions, /margin:0 0 0 auto/);
    const buttons = rule('.direct-menu-toolbar .direct-actions button');
    assert.match(buttons, /width:auto/);
    assert.match(buttons, /min-height:32px/);
    assert.match(buttons, /font-size:14px/);
    assert.match(rule('.direct-menu-toolbar'), /position:sticky/);
    assert.match(rule('.direct-menu-toolbar .direct-tabs'), /overflow-x:auto/);
});
test('remote handle is a double chevron contained within the existing 24px page gutter', () => {
    const handle = rule('.remote-drawer-tab');
    assert.match(handle, /width:24px/);
    assert.match(handle, /height:44px/);
    assert.match(handle, /padding:0/);
    const layout = fs.readFileSync(path.join(root, 'Components/Layout/MainLayout.razor'), 'utf8');
    const button = layout.match(/<button[^>]*class="remote-drawer-tab"[^>]*>[\s\S]*?<\/button>/)[0];
    assert.match(button, /aria-label="Open remote"/);
    assert.match(button, /popovertarget="remote-drawer"/);
    assert.match(button, /<svg .*aria-hidden="true"/);
    assert.match(button, /m11 6-6 6 6 6m8-12-6 6 6 6/);
    assert.doesNotMatch(button, /‹ Remote/);
});
test('signed text inputs retain the explicit width and typography of slider numbers', () => {
    const input = rule('.direct-panel input.direct-number-value[type=text]');
    assert.match(input, /width:76px/);
    assert.match(input, /text-align:center/);
    assert.match(input, /font-variant-numeric:tabular-nums/);
    assert.match(css, /\.direct-panel \.direct-number-value \{ font-family:inherit; font-size:18px; font-weight:700/);
});
test('live command status reserves one compact line to avoid moving controls on each request', () => {
    const status = rule('.topbar .direct-live-status');
    assert.match(status, /height:18px/);
    assert.match(status, /min-height:18px/);
    assert.doesNotMatch(rule('.direct-live-status:empty'), /display:none/);
});
