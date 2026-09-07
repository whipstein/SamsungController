# Menu definition file format tutorial

SamsungController menu profiles use one versioned data model that can be stored
as either YAML or JSON. YAML is the default because it is compact, supports
comments, and is easier to edit by hand. JSON is useful when another program
generates or validates the topology.

- Use `.yaml` or `.yml` for YAML.
- Use `.json` for JSON.
- **Build & Verify > Create TV profile** creates YAML unless JSON is selected.
- Loading, editing, and recording an existing profile keep that file's format.
  Saving a JSON profile does not turn it into YAML. Verification never saves to
  the menu file.
- For a path without a recognized extension, SamsungController detects a JSON
  object by its leading `{`; otherwise it treats the file as YAML. A new file
  without a recognized extension defaults to YAML.

Both formats accept the same field names and run through the same parser,
reference checks, route checks, and safety limits. Field names are
case-insensitive, but the spelling shown here is the normalized form written by
SamsungController. Unknown and duplicate fields are rejected rather than
silently ignored.

When a JSON file fails to load, syntax, schema, and validation errors include
one-based source line and column numbers. Multiple validation problems remain
on separate lines in the Build & Verify error panel.

## Inspect and debug files

**Build & Verify > Schema inspector** evaluates one file or recursively scans a
directory without sending any commands to the TV. The report distinguishes
blocking errors from warnings and shows the parsed identity, model/firmware,
node count, anchors, explicit seed traversals, topology-generated routes, and
configurations. Personal verification sidecars are not included. Use **Inspect active file** or **Inspect repository
catalog** as shortcuts; a valid result can be loaded directly from the report.

The packaged and source CLI exposes the same evaluator:

```text
samsungctl menu validate menu-definitions
samsungctl menu validate menu-definitions/my-tv.json
```

The first command checks every YAML/JSON file below the directory. Exit code 0
means all files are valid; exit code 2 means at least one file is invalid or no
definition was found. The inspector regenerates topology routes before
validation, matching the effective menu the application will navigate. A stale
generated-route warning means the file is still usable and that the stored
derived routes will be refreshed on load.

## Start safely

The guided UI is the recommended editor. It preserves generated route metadata,
recalculates verification requirements, and reports invalid fields before it
sends any key. Raw editing is useful for bulk generation or review:

1. Copy the file and keep its extension.
2. Edit stable IDs only when creating new objects. Renaming an ID breaks every
   reference to it and should normally be done through the UI.
3. Load the file with **Build & Verify > Load & validate**. Parse and validation
   errors are shown without sending TV commands.
4. Review the recalculated checklist on **Display Verification**. A behavioral
   edit invalidates only representative groups whose fingerprints changed.

Display-verification evidence and named current-TV states do not belong in this
file. The application stores evidence in local `menu-verifications/` sidecars
and saved values in local application settings. **Export & use structure** always
produces a portable file without either kind of local state and makes the copy
the active authoring source.

This is a strict storage boundary: a menu file is reusable topology, while a
`<menu-id>.verification.json` sidecar is personal evidence for one or more
display combinations. Running or approving a verification check does not edit,
copy, or relocate the referenced YAML/JSON menu file. SamsungController applies
matching sidecar fingerprints only to its in-memory navigation model.

## The topology in one picture

```text
menu structure
├── identity and display context
├── configurations (optional alternate menu layouts)
├── externalStates (equipment context not reported by the TV)
├── timing (system-wide waits)
├── nodes (ordered on-screen tree and control behavior)
├── anchors (known-state recovery and return behavior)
└── transitions (explicit or generated key routes)
```

`nodes` are a recursive tree. Each submenu stores its immediately visible rows
in a `children` array, in the same order they appear on the TV. A child's
position in that array determines the generated Up/Down offset. Nesting alone
does not prove that a route works. Anchors and transitions supply the executable
keys. Local display-verification sidecars record visually tested behavior
without making the structure display-specific. The containing
array is the only way to declare a parent; a separate `parent` field is not part
of the schema.

## Complete YAML example

```yaml
version: 1
id: s95f-1296
name: S95F · firmware 1296
model: S95F

context:
  firmware: "1296"

configurations:
  - id: default
    name: Default menu
    conditions: Game Mode = Off

externalStates:
  - id: pgen-output-format
    label: Color format
    defaultValue: RGB
    options: [RGB, YCbCr422, YCbCr444]
  - id: hdmi-bit-depth
    label: Bit depth
    defaultValue: 8-bit
    options: [8-bit, 10-bit]

timing:
  defaultDelay: 150ms
  screenChangeDelay: 800ms
  returnDelay: 300ms
  adjustmentDelay: 75ms

nodes:
  - id: tv-interface
    label: TV interface
    controlType: submenu
    children:
      - id: normal-video
        label: Normal video
        controlType: submenu
      - id: settings
        label: Settings
        controlType: submenu
        children:
          - id: picture
            label: Picture
            controlType: submenu
            children:
              - id: picture-mode
                label: Picture Mode
                controlType: selection
                defaultValue: Filmmaker Mode
                options:
                  - Standard
                  - Movie
                  - Filmmaker Mode
              - id: smart-calibration
                label: Smart Calibration
                controlType: action
              - id: brightness
                label: Brightness
                description: Whole-number steps shown by the TV
                controlType: slider
                defaultValue: "50"
                minimumValue: 0
                maximumValue: 100
              - id: hdmi-black-level
                label: HDMI Black Level
                controlType: selection
                defaultValue: Auto
                options: [Auto, Low, Normal]
                disabledWhen:
                  - externalState: pgen-output-format
                    equals: YCbCr422
                  - externalState: pgen-output-format
                    equals: YCbCr444
              - id: contrast-enhancer
                label: Contrast Enhancer
                controlType: switch
                defaultValue: off
                disabledWhen:
                  - setting: picture-mode
                    equals: Filmmaker Mode
                hiddenWhen:
                  - setting: picture-mode
                    equals: Standard
              - id: reset-picture
                label: Reset Picture
                controlType: confirmation
                defaultValue: Cancel
                options: [Reset, Cancel]

anchors:
  - id: normal-video-anchor
    label: Return to normal video
    target: normal-video
    configuration: default
    validationSource: settings
    returnStrategy:
      menuRoot: settings
      atMenuRoot:
        steps:
          - key: KEY_RETURN
      belowMenuRoot:
        steps:
          - key: KEY_MENU
          - key: KEY_RETURN
      overrides:
        - node: reset-picture
          steps:
            - key: KEY_RETURN
            - key: KEY_MENU
            - key: KEY_RETURN
    steps:
      - key: KEY_RETURN

transitions:
  - id: open-settings
    from: normal-video
    to: settings
    configuration: default
    description: Open Settings from normal video
    steps:
      - key: KEY_MENU
        delay: 1s
```

Verification fields are intentionally absent. The app never adds them to a menu
definition; visual checks are written only to the personal sidecar.

## Equivalent JSON example

This shorter JSON file expresses the same core concepts. JSON strings need
double quotes, arrays need commas, and comments or trailing commas are not
allowed.

```json
{
  "version": 1,
  "id": "s95f-1296",
  "name": "S95F · firmware 1296",
  "model": "S95F",
  "context": {
    "firmware": "1296"
  },
  "configurations": [
    {
      "id": "default",
      "name": "Default menu",
      "conditions": "Game Mode = Off"
    }
  ],
  "externalStates": [
    {
      "id": "pgen-output-format",
      "label": "Color format",
      "defaultValue": "RGB",
      "options": ["RGB", "YCbCr422", "YCbCr444"]
    },
    {
      "id": "hdmi-bit-depth",
      "label": "Bit depth",
      "defaultValue": "8-bit",
      "options": ["8-bit", "10-bit"]
    }
  ],
  "timing": {
    "defaultDelay": "150ms",
    "screenChangeDelay": "800ms",
    "returnDelay": "300ms",
    "adjustmentDelay": "75ms"
  },
  "nodes": [
    {
      "id": "tv-interface",
      "label": "TV interface",
      "controlType": "submenu",
      "children": [
        {
          "id": "normal-video",
          "label": "Normal video",
          "controlType": "submenu"
        },
        {
          "id": "settings",
          "label": "Settings",
          "controlType": "submenu",
          "children": [
            {
              "id": "picture",
              "label": "Picture",
              "controlType": "submenu",
              "children": [
                {
                  "id": "picture-mode",
                  "label": "Picture Mode",
                  "controlType": "selection",
                  "defaultValue": "Filmmaker Mode",
                  "options": ["Standard", "Movie", "Filmmaker Mode"]
                },
                {
                  "id": "smart-calibration",
                  "label": "Smart Calibration",
                  "controlType": "action"
                },
                {
                  "id": "brightness",
                  "label": "Brightness",
                  "controlType": "slider",
                  "defaultValue": "50",
                  "minimumValue": 0,
                  "maximumValue": 100
                }
              ]
            }
          ]
        }
      ]
    }
  ],
  "anchors": [
    {
      "id": "normal-video-anchor",
      "label": "Return to normal video",
      "target": "normal-video",
      "configuration": "default",
      "validationSource": "settings",
      "returnStrategy": {
        "menuRoot": "settings",
        "atMenuRoot": {
          "steps": [{ "key": "KEY_RETURN" }]
        },
        "belowMenuRoot": {
          "steps": [{ "key": "KEY_MENU" }, { "key": "KEY_RETURN" }]
        }
      },
      "steps": [{ "key": "KEY_RETURN" }]
    }
  ],
  "transitions": [
    {
      "id": "open-settings",
      "from": "normal-video",
      "to": "settings",
      "configuration": "default",
      "steps": [{ "key": "KEY_MENU", "delay": "1s" }]
    }
  ]
}
```

## Root fields

| Field | Required | Meaning |
| --- | --- | --- |
| `version` | Yes | Schema version. The only supported value is `1`. |
| `id` | Yes | Stable profile ID used for per-profile settings. |
| `name` | Yes | Human-readable profile name. |
| `model` | Yes | TV model or model family. |
| `context` | No | Firmware version associated with the menu definition. |
| `configurations` | No | Named alternate menu layouts. |
| `externalStates` | No | User-selected equipment or signal context the TV protocol does not report. |
| `timing` | No | Default waits; omitted fields use built-in defaults. |
| `nodes` | Yes | At least one ordered root topology node; submenu descendants are nested under `children`. |
| `anchors` | No | Deterministic known-state/recovery scripts. |
| `transitions` | No | Directed key sequences between nodes. |

IDs are case-insensitively unique within their collection. They must begin with
a letter or underscore and contain only letters, digits, `_`, `-`, or `.`.
Labels and descriptions may contain normal display text. Keep IDs stable even
when a TV label changes.

### `context`

| Field | Default | Guidance |
| --- | --- | --- |
| `firmware` | `unrecorded` | Quote numeric-looking firmware in hand-authored files for clarity. |

Firmware is the only supported context field. The TV model remains at the root
under `model`. Remove `signal`, `pictureMode`, and `input` from older context
blocks; unknown fields produce a schema error. Generated filenames use model
and firmware, for example `s95f-1296.yaml` or `s95f-1296.json`.

Display Verification binds the structure to the model and firmware in a local
sidecar. Named menu configurations, setting conditions, and external equipment
states remain separate features for menus whose available rows change.

### `configurations`

Each entry requires `id` and `name`; `conditions` is optional human-readable
text. Configurations are appropriate when the menu reorders or changes in a way
that cannot be expressed by `hiddenWhen`. The application does not evaluate
`conditions` or read those values from the TV. The selected active configuration
is stored in local application settings, not inside this file.

An anchor or transition with `configuration` is eligible only when that layout
is active. Omitting `configuration` makes the behavior universal, so do that
only after verifying it in every intended layout.

### `externalStates`

Use an external state for a fact that changes row availability but cannot be
read through the Samsung remote protocol—for example, a PGen output format.
Each entry requires a stable `id`, a `label`, a `defaultValue`, and one or more
ordered `options`. The default must occur in the options.

The app renders these states as persistent selectors in the header. Change the
external equipment first, then choose its matching value in SamsungController.
The selection is remembered per menu definition. Changing it while the app
expects to be inside a TV menu marks that menu position unknown, because the
external signal may have changed what is on screen.

External state is intentionally separate from `configurations`. Use a
configuration when the menu is actually reordered and needs a different route
set. Use an external state when a row remains in cursor order but becomes gray
or hidden. Newly created menu definitions include separate **Color format**
and **Bit depth** selectors. Existing files can add `hdmi-bit-depth` to their
`externalStates` array as shown above, then use **Reload menu definition**.
Keep `pgen-output-format` as the existing color-format ID so its conditions and
remembered values still work. The header displays it as `Color format` even if
an existing file retains the label `PGen output format`; there is no need to
rename it or disturb that definition's existing verification fingerprints.

### Independent color format and bit depth

The two selectors are independent: RGB, YCbCr422, and YCbCr444 can each be paired
with 8-bit or 10-bit. Changing one does not change the other, send remote keys,
or invalidate existing verification merely because a different value is selected.
They describe the actual source output; selecting 10-bit does not configure your
generator, imply HDR, or choose a TV gamma curve. Confirm both values against
your equipment rather than relying on the template defaults of RGB and 8-bit.

For a row that is absent at 8-bit, use:

```yaml
hiddenWhen:
  - externalState: hdmi-bit-depth
    equals: 8-bit
```

Use `disabledWhen` instead if the row stays visible and gray. Conditions on bit
depth alone apply regardless of color format. Keep an HDMI Black Level rule
that depends only on RGB/YCbCr tied to `pgen-output-format` alone. These condition
lists mean **OR**, not AND: adding a color-format condition would also hide or
disable the row whenever that color format is selected.

If the *choices inside a selection control* differ, define two adjacent,
mutually exclusive versions of that row. Each has a unique ID, the same visible
label, its own ordered `options` and `defaultValue`, and the opposite bit depth
in `hiddenWhen`. Only the visible variant contributes to navigation offsets.
For example, this models an observed display with BT.1886/2.2 at 8-bit and only
ST.2084 at 10-bit. Use the exact choices and order observed on your own display;
this is not a universal bit-depth-to-gamma rule:

```yaml
# These two entries occupy the same visible row, inside their parent's children.
- id: gamma-8bit
  label: Gamma
  controlType: selection
  defaultValue: BT.1886
  options: [BT.1886, "2.2"]
  hiddenWhen:
    - externalState: hdmi-bit-depth
      equals: 10-bit
- id: gamma-10bit
  label: Gamma
  controlType: selection
  defaultValue: ST.2084
  options: [ST.2084]
  hiddenWhen:
    - externalState: hdmi-bit-depth
      equals: 8-bit
- id: bt.1886
  label: BT.1886
  controlType: slider
  defaultValue: 0
  minimumValue: -3
  maximumValue: 3
  hiddenWhen:
    - externalState: hdmi-bit-depth
      equals: 10-bit
    - setting: gamma-8bit
      equals: "2.2"
- id: st.2084
  label: ST.2084
  controlType: slider
  defaultValue: 0
  minimumValue: -3
  maximumValue: 3
  hiddenWhen:
    - externalState: hdmi-bit-depth
      equals: 8-bit
```

The fixed ST.2084-only control does not offer a value change. Guided selection
verification uses another available multi-choice control instead of trying to
change it. The ST.2084 adjustment slider is still adjustable and verifiable;
in this example it occupies the same cursor position as the BT.1886 slider at
8-bit. Slider ranges/defaults must also match the actual display.

In JSON, the condition is
`"hiddenWhen": [{ "externalState": "hdmi-bit-depth", "equals": "8-bit" }]`.
In the outline editor it is `hiddenWhen=external:hdmi-bit-depth=8-bit`.
The node editor also lists **Bit depth** as a condition source after the state
has been declared. If other rows depend on a gamma value, point their `setting`
conditions at the appropriate variant ID and give them matching bit-depth
visibility rules. Check the actual current values in **Enter current settings** after
changing the source; the remote protocol cannot read the TV's resulting values.

## Node fields and control types

Every node requires `id` and `label`. These fields are optional:

| Field | Meaning |
| --- | --- |
| `children` | Ordered array of the rows immediately contained by a submenu. Valid only on `submenu` nodes. |
| `description` | Notes for users and future development. |
| `controlType` | Interaction type; defaults to `submenu`. |
| `defaultValue` | Expected value after reset/startup. Required for value-bearing controls; forbidden for `submenu` and `action`. |
| `defaultValueWhen` | Ordered external-signal default overrides. Every condition in a rule must match; the first matching rule wins. `defaultValue` remains the fallback. |
| `minimumValue`, `maximumValue` | Numeric slider boundaries; both are required for sliders only. |
| `options` | Ordered values for choice controls. |
| `disabled` | Set to `true` when this row is always visible but permanently gray/unavailable. Descendants inherit this state. |
| `disabledWhen` | OR-list of conditions that leave this row present but gray/unavailable. Descendants inherit this state. |
| `hiddenWhen` | OR-list of conditions that remove this row and all descendants. |

### Defaults that depend on HDMI input or signal

Use **Build & Verify → Define menu tree → edit a control → Signal-dependent
defaults** to add rules without editing raw files. For each rule, select the
color format, bit depth, or other declared external state, then enter the
default value. **Any** leaves that state unconstrained. Use the up/down buttons
to put specific combinations first, then **Save/Update** the menu item. Remove
all rules to return to a single unconditional default. The outline editor
preserves these rules when renaming or reorganizing existing items; use the node
editor or the raw file to change the rules themselves.

Example values below illustrate the syntax, not recommended TV settings:

```yaml
- id: brightness
  label: Brightness
  controlType: slider
  minimumValue: 0
  maximumValue: 50
  defaultValue: 25
  defaultValueWhen:
    - when:
        pgen-output-format: RGB
        hdmi-bit-depth: 10-bit
      value: 40
    - when:
        hdmi-bit-depth: 10-bit
      value: 35
```

This uses 40 for RGB + 10-bit, 35 for other color formats at 10-bit, and 25 when
neither rule matches. Conditions within `when` are **AND**. Rule order is
significant: putting the broad 10-bit rule first would mask the RGB-specific
rule. Unlike `disabledWhen` and `hiddenWhen`, these are not OR-lists of individual
conditions.

The equivalent JSON node is:

```json
{
  "id": "brightness",
  "label": "Brightness",
  "controlType": "slider",
  "minimumValue": 0,
  "maximumValue": 50,
  "defaultValue": "25",
  "defaultValueWhen": [
    {
      "when": { "pgen-output-format": "RGB", "hdmi-bit-depth": "10-bit" },
      "value": "40"
    },
    {
      "when": { "hdmi-bit-depth": "10-bit" },
      "value": "35"
    }
  ]
}
```

Every key in `when` must reference an ID declared in the root `externalStates`
list, and every condition value must be one of that state's options. This can
include a physical HDMI connector as well as color format. For example, append
this state to the existing list, using the input names available on your TV:

```yaml
- id: hdmi-input
  label: HDMI input
  defaultValue: HDMI 1
  options: [HDMI 1, HDMI 2]
```

Then a rule can use `when: { hdmi-input: HDMI 2, hdmi-bit-depth: 10-bit }`.
The new input selector appears beside the other external signal controls.
Like those selectors, it records the source you selected on the equipment;
it does **not** switch the TV input or query the TV.

Conditional defaults work for sliders, switches, selections, submenu/indexed
selections, and confirmation dialogs' initially highlighted choices. They do
not change slider bounds or selection options. Each override must be legal for
its control, just like the fallback: within the slider bounds, `on`/`off` for
a switch, or one of the declared choices. Submenus and actions cannot have
defaults. Each control supports up to 20 rules, each matching up to 20 external
states. Empty and duplicate conditions are rejected with the node and rule
number in the error.

Changing the header's signal selections sends **no TV commands**. Default-based
estimates update for the new context. Saved/entered current values, applied
values, and staged targets are retained **for their own input combination**;
switching restores the selected combination, not the previous signal's values.
All combinations can be saved and loaded in [one calibration file](calibration-target-files.md).
Use **Enter current settings** to confirm the actual baseline when switching
sources. An explicit **Reset to defaults** operation resets the TV
and uses the matching defaults for the selected signal. Guided verification
restores the prior value after testing. New or changed rules reopen the related
control verification, while merely choosing another signal does not rewrite
the menu file or discard its verification.

Supported `controlType` values are:

| Type | Required behavior data | How SamsungController treats it |
| --- | --- | --- |
| `submenu` | No `defaultValue`, bounds, or options | Enter opens a child menu. |
| `slider` | Numeric `defaultValue`, `minimumValue`, `maximumValue` | Sends one Left/Right step per whole-number change. |
| `selection` | `defaultValue` plus one or more ordered `options` | Opens choices, moves relative to the predicted value, and selects. |
| `submenu-selection` | Same as selection | Also sends Return after choosing because the value list stays open. |
| `indexed-selection` | Options and at least one immediately following sibling slider | Renders options as fixed grid rows with following sliders as columns. |
| `switch` | `defaultValue` equal to `on` or `off` | Sends Select and toggles the predicted state. |
| `confirmation` | `defaultValue` plus at least two ordered `options` | Models an action dialog such as Reset/Cancel, not a persistent setting. |
| `action` | No default, bounds, options, or children | Models a row that starts a TV function. Routes stop with the row highlighted and never press OK automatically. |

Options are case-insensitively unique, limited to 100 entries and 100 characters
per entry, and must contain the declared default. For an indexed selection, keep
its sliders consecutive under the same parent immediately after the selector.

### Conditional rows

For a row that is always present but never selectable, use the boolean
`disabled` field:

```yaml
disabled: true
```

```json
"disabled": true
```

The default is `false`. Do not combine `disabled: true` with `disabledWhen` on
the same node; the conditional rule would be redundant. A permanently disabled
submenu automatically makes all of its descendants unavailable. The visual
editor exposes the same behavior as **Always disabled**, and the outline editor
accepts either `{submenu; disabled}` or `{submenu; disabled=true}`.

A disabled row remains part of the TV's cursor order, so generated Up/Down
offsets count it even though SamsungController will not execute it as a target.
Only a hidden row is removed from directional offsets.

Use `disabledWhen` only when availability depends on another modeled setting or
declared external state.

Both conditional fields contain objects with `equals` and exactly one source:
`setting` for a TV menu value, or `externalState` for declared equipment
context:

```yaml
disabledWhen:
  - setting: game-mode
    equals: on
  - setting: picture-mode
    equals: Dynamic
  - externalState: pgen-output-format
    equals: YCbCr422
```

```json
"disabledWhen": [
  { "setting": "game-mode", "equals": "on" },
  { "setting": "picture-mode", "equals": "Dynamic" },
  { "externalState": "pgen-output-format", "equals": "YCbCr422" }
]
```

Multiple entries use **OR**, not AND: any match activates the behavior. The
referenced setting must be a slider, selection, submenu selection, indexed
selection, or switch. A condition cannot refer to its own node. Selection values
must appear in that setting's options; switch values must be `on` or `off`.
An `externalState` must exist at the document root and the compared value must
appear in its options.

In the Build & Verify outline editor, the same external condition can be written
as `disabledWhen=external:pgen-output-format=YCbCr422`. The node editor lists
external sources alongside TV settings. Display Verification creates one
representative guided check for external disabled rows and one for external
hidden rows; it asks you to set both the hardware and header selector first.
For a disabled control, the test highlights the named row without pressing Enter
on that control or changing its value. Gray siblings still count as cursor stops;
hidden siblings do not. For an external-state hidden row, the test highlights the
next visible sibling control at the missing row's location, or the previous
visible control if none follows. It names both the highlighted control and the
item that should be absent. For example, when BT.1886 is the representative
hidden under 10-bit input, the ST.2084 replacement slider is highlighted instead.
Same-named variants are identified by node ID and, for selections, their options.
No setting is changed.
The current menu state reflects the visible control, never the missing row.
Submenus are not entered just to inspect a missing sibling. If only submenus or
no rows remain visible, the test explicitly asks for manual inspection of the
containing menu. Disabled submenus also use containing-menu inspection.

When a submenu is disabled, every descendant is also unavailable automatically.
Define `disabledWhen` only on that submenu; repeating the same condition on its
children is unnecessary. When the controlling value enables the submenu again,
its descendants become available and generated routes use the inherited state.

Conditions are currently OR-only. Use separate configurations if a combination
requires AND logic or cannot be represented by one modeled value.

## Timing and key steps

`timing` has four waits plus its verification state:

| Field | Built-in default | Used after |
| --- | ---: | --- |
| `defaultDelay` | `150ms` | Up/Down navigation and other ordinary/custom keys. |
| `screenChangeDelay` | `800ms` | Menu, Enter/OK, Home, Exit, and Source. |
| `returnDelay` | `300ms` | Return. |
| `adjustmentDelay` | `75ms` | Left/Right value changes for sliders, switches, and selections. |
| `verified` | `true` for a new profile | Whether the four global waits passed their dedicated visual test. |

System waits must be 50–30000 ms. Duration values must include `ms` or `s`, such
as `250ms` or `1.5s`.

A step object supports:

| Field | Required/default | Meaning |
| --- | --- | --- |
| `key` | Required | Samsung key name such as `KEY_MENU`. |
| `action` | `Click` | `Click`, `Press`, or `Release`, case-insensitive. |
| `repeat` | `1` | Integer from 1 through 100. |
| `delay` | System timing | Per-step override greater than zero and no more than 30 seconds. |

```json
{ "key": "KEY_RIGHT", "action": "Click", "repeat": 2, "delay": "250ms" }
```

The TV does not acknowledge menu execution. Delays are predictive scheduling,
and every sequence still requires visual validation.

## Anchors and return strategies

An anchor requires `id`, `label`, `target`, and a nonempty `steps` array. It may
also have `configuration`, `verified`, `description`, `validationSource`, and
`returnStrategy`.

- `target` is the known state after the ordinary anchor `steps` succeed.
- `validationSource` is the modeled location used to prepare a visual anchor
  test.
- `verified` defaults to false.
- Ordinary `steps` remain the fallback sequence.

A `returnStrategy` requires:

- `menuRoot`: the base menu, such as `settings`.
- `atMenuRoot`: `{ verified, steps }` used exactly at that node.
- `belowMenuRoot`: `{ verified, steps }` used at descendants.
- optional `overrides`: exact-node `{ node, verified, steps }` scripts that take
  priority over the two defaults.

Each script is independently verified. The menu root must exist and must differ
from the anchor target.

## Transitions

A transition requires `id`, `from`, `to`, and nonempty `steps`. Optional fields
are `configuration`, `verified`, `description`, and `returnSteps`.
`returnSteps` is the target-specific return-to-video phase recorded with the
original traversal; it shares that transition's verification result.

The application also writes these fields for topology-generated transitions:

- `generatedFromTopology`
- `topologySeed`
- `validationGroup`
- `validationRoute`

Treat them as generated metadata. Do not add or change them manually. Editing
the tree or seed route causes SamsungController to regenerate the affected
routes and verification plan.

## Local display-verification sidecars

Display Verification writes local JSON under the platform configuration
directory's `menu-verifications/` folder. A sidecar is keyed by the stable menu
structure ID and can contain separate profiles for multiple model/firmware
combinations. Each check stores an app-defined
ID, a SHA-256 fingerprint, and an ISO-8601 timestamp.

Fingerprints include the display combination and relevant structure behavior.
Controls sharing an interaction type are fingerprinted as one representative
group. Conditional rows use permanent-disabled, conditional-disabled, and
hidden behavior classes. Changing only a slider's bounds reopens the shared
slider check without invalidating unrelated routes. Sidecars are local evidence:
do not add them to the repository menu catalog or distribute them as proof for
another physical display.

Verification fingerprints now use model and firmware only. Evidence produced
with the older signal/picture-mode/input identity will no longer match; review
the required checks in Display Verification after updating an older file.

## YAML and JSON differences

| Concern | YAML | JSON |
| --- | --- | --- |
| Default for new profiles | Yes | Select explicitly |
| Comments | Supported with `#` | Not supported |
| Trailing commas | Not applicable | Rejected |
| Quoting | Often optional | All object keys and strings require double quotes |
| Numbers/booleans | Native scalars | Native scalars |
| Duration values | String such as `800ms` | String such as `"800ms"` |
| Save behavior | Rewritten as normalized YAML | Rewritten as indented normalized JSON |

Saving through the app is semantic, not text-preserving: comments, custom
spacing, flow style, and property order may be normalized. The topology and all
supported settings are preserved. Keep explanatory prose in `description` when
it must survive an application save; YAML comments are best treated as
maintainer-only notes.

## Common errors

- **Unknown field:** check spelling and whether the field belongs at that level.
- **Missing node reference:** define the referenced node in `nodes`, or correct the `from`, `to`, `target`,
  `setting`, `menuRoot`, or override node before referencing it.
- **Missing external-state reference:** declare the `externalState` at the root
  and include the compared value in its `options`.
- **Choice default not found:** add the exact default to `options`.
- **Invalid slider:** provide both bounds and keep the default inside them.
- **Invalid JSON:** remove comments/trailing commas and quote keys and strings.
- **Route unavailable:** verify the route for the active configuration and make
  sure conditional visibility matches SamsungController's predicted values.

For navigation confidence, generated-route behavior, and the complete guided
workflow, continue with [Menu definitions and predicted navigation](menu-model.md).
