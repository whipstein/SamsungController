# Calibration files for all input conditions

A single `.samsung-calibration.json` file can hold settings for every saved
input-condition combination. Load it once: selecting **External HDMI Signal**
color format or bit depth automatically selects the matching saved values.
Other declared external states, such as a physical HDMI input, participate too.

The shared YAML/JSON menu definition still describes the topology and
conditional factory defaults. Personal current settings and calibration targets
are stored separately from both the menu and its verification.

## Save and load from the UI

1. Select the display/menu combination and the correct External HDMI Signal values.
   These selectors describe the signal; they do not change your source equipment.
2. Under **Menu → Enter current settings**, enter values already on the TV,
   then choose **Save entered values**. Repeat for each input combination you
   want to record. Values and named states are kept separately for each combination.
3. Choose **Download all current settings**. One file includes all saved
   current-value combinations for this display and menu, including indexed grids.
   Unsaved drafts and unconfirmed defaults are not exported as known settings.
4. To restore the whole collection, use **Load calibration JSON as current**
   once. This immediately installs every included combination as a local current
   baseline. Use this only when the TV actually has those settings. Manual
   entry still uses a draft and requires **Save entered values**.
5. To use that same collection as desired calibration settings instead, open
   **Adjust TV** and use **Load calibration JSON to apply**. Every combination
   is staged and saved locally; current baselines are left unchanged.
6. Switching signal selectors restores the matching current values and targets.
   Select **Apply** to update only the currently selected combination.
   Loading or switching never sends TV commands, even in immediate-update mode.
7. **Download all targets** exports all saved target combinations into one file.
   Staged target edits are remembered locally when you edit them.

You can load the same file once into each role if it represents both the
existing TV settings and the desired calibration. Loading targets never silently
asserts that the TV already contains them. Saved collections survive an app
restart. A combination without saved current values uses its conditional defaults
as explicitly labeled assumptions, not values from the previously selected input.

Imports replace the selected role's values for combinations included in the file.
Other combinations and the opposite role are preserved. In a partial combination,
omitted current values fall back to assumed defaults; omitted targets have no
staged adjustment. The entire file is validated before any combination is saved.

## Version 2 format

```json
{
  "version": 2,
  "name": "My display — all inputs",
  "definitionId": "my-tv-menu",
  "definitionName": "My TV menu",
  "model": "My TV",
  "context": { "firmware": "1296" },
  "exportedAtUtc": "2026-09-06T12:00:00Z",
  "values": [],
  "conditionValues": [
    {
      "conditions": {
        "pgen-output-format": "RGB",
        "hdmi-bit-depth": "8-bit"
      },
      "values": [
        { "nodeId": "brightness", "value": "25" },
        {
          "nodeId": "white-balance-20-point-red",
          "value": "2",
          "selectorNodeId": "white-balance-20-point-interval",
          "selectorValue": "5%"
        }
      ]
    },
    {
      "conditions": {
        "pgen-output-format": "RGB",
        "hdmi-bit-depth": "10-bit"
      },
      "values": [
        { "nodeId": "brightness", "value": "40" },
        {
          "nodeId": "white-balance-20-point-red",
          "value": "4",
          "selectorNodeId": "white-balance-20-point-interval",
          "selectorValue": "5%"
        }
      ]
    },
    {
      "conditions": {
        "pgen-output-format": "YCbCr422",
        "hdmi-bit-depth": "10-bit"
      },
      "values": [
        { "nodeId": "brightness", "value": "35" }
      ]
    }
  ]
}
```

Use the IDs and options from your menu, not necessarily those in this example.

- `version`, `name`, `definitionId`, and nonempty `conditionValues` are required
  for an all-conditions file. Leave top-level `values` empty or omit it.
- Each `conditions` map must specify **every external state declared by the
  menu**. Matching is exact, case-insensitive, and independent of map order.
  There are no wildcard/priority rules here: each stored combination is unambiguous.
  A menu without external states uses `"conditions": {}`.
- If your menu declares `hdmi-input`, add it to every map, for example
  `"hdmi-input": "HDMI 1"`. Unknown state IDs and unsupported options are rejected.
- Every combination needs at least one value. Ordinary controls use `nodeId`
  and `value`; indexed grid cells also need both `selectorNodeId` and
  `selectorValue`.
- Node/selector IDs and selection values must exist in the menu. Sliders must
  contain whole numbers inside their declared boundaries. Duplicate combinations
  or duplicate control/indexed keys within a combination are rejected.
- Limits: 2 MB per file, 128 combinations, 20 conditions per combination,
  5,000 values per combination, and 50,000 values total. JSON syntax errors show
  a one-based line and column; invalid values identify the combination.
- Import is bound locally to the selected display and menu. The portable file
  contains no TV address, tokens, or verification records.

Older version-1 files containing only top-level `values` can still be loaded.
Their values are bound to the currently selected input combination; they do not
apply to every signal condition. New downloads use version 2.

## Safe application

Relative controls require an accurate starting value. SamsungController does
not read the actual slider values from the TV. If you change settings with the
physical remote, record the new current values before applying a calibration.

Large Apply, reset-to-defaults, and reset/apply operations show a red sticky
progress strip. Select **Cancel active update** if the visible TV state differs
from the expected state. Cancellation prevents subsequent keys after any
already in-flight send or wait and sends no automatic cleanup commands.
Inspect the TV and record the current values before retrying a partial operation.
