// Structural/CSS contracts; these do not replace a rendered browser check.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.join(__dirname, '../../src/SamsungController.Web');
test('recall confirmation wraps inside its box beside a fixed large checkbox in both layouts', () => {
    const markup = fs.readFileSync(path.join(root, 'Components/Shared/SavedSettingsStates.razor'), 'utf8');
    const css = fs.readFileSync(path.join(root, 'Components/Shared/SavedSettingsStates.razor.css'), 'utf8');
    const compact = fs.readFileSync(path.join(root, 'wwwroot/compact.css'), 'utf8');
    const label = markup.match(/<label class="saved-settings-conditions">[\s\S]*?<\/label>/)?.[0];
    assert(label);
    assert.match(label, /aria-label="Confirm saved state conditions"/);
    assert.match(label, /\/><span>I confirmed[^<]*<\/span><\/label>/);
    assert.match(css, /\.saved-settings-conditions \{[^}]*grid-template-columns: 28px minmax\(0, 1fr\)/);
    assert.match(css, /input\[type="checkbox"\] \{[^}]*width: 28px;[^}]*height: 28px;[^}]*padding: 0/);
    assert.match(css, /\.saved-settings-conditions > span \{[^}]*min-width: 0;[^}]*font: inherit;[^}]*overflow-wrap: anywhere/);
    assert.match(compact, /\.saved-settings :is\(input:not\(\[type=checkbox\]\),select\)/);
});
