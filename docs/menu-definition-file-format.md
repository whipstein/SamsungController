# Menu definition file format tutorial

SamsungController menu profiles use one versioned data model that can be stored
as either YAML or JSON. YAML is the default because it is compact, supports
comments, and is easier to edit by hand. JSON is useful when another program
generates or validates the topology.

- Use `.yaml` or `.yml` for YAML.
- Use `.json` for JSON.
- **Build & Verify > Create TV profile** creates YAML unless JSON is selected.
- Loading, editing, recording, and verifying an existing profile keep that
  file's format. Saving a JSON profile does not turn it into YAML.
- For a path without a recognized extension, SamsungController detects a JSON
  object by its leading `{`; otherwise it treats the file as YAML. A new file
  without a recognized extension defaults to YAML.

Both formats accept the same field names and run through the same parser,
reference checks, route checks, and safety limits. Field names are
case-insensitive, but the spelling shown here is the normalized form written by
SamsungController. Unknown and duplicate fields are rejected rather than
silently ignored.

## Start safely

The guided UI is the recommended editor. It preserves generated route metadata,
recalculates verification requirements, and reports invalid fields before it
sends any key. Raw editing is useful for bulk generation or review:

1. Copy the file and keep its extension.
2. Edit stable IDs only when creating new objects. Renaming an ID breaks every
   reference to it and should normally be done through the UI.
3. Load the file with **Build & Verify > Load & validate**. Parse and validation
   errors are shown without sending TV commands.
4. Review the recalculated checklist on **File Verification**. A behavioral
   edit invalidates only representative groups whose fingerprints changed.

Never copy a `verification` block from another file or manufacture its
fingerprints. It is evidence for one exact file/display combination, not a flag
that should be forced to true.

## The topology in one picture

```text
menu definition
├── identity and display context
├── configurations (optional alternate menu layouts)
├── timing (system-wide waits)
├── nodes (ordered on-screen tree and control behavior)
├── anchors (known-state recovery and return behavior)
├── transitions (explicit or generated key routes)
└── verification (display-bound evidence maintained by the app)
```

`nodes` are a recursive tree. Each submenu stores its immediately visible rows
in a `children` array, in the same order they appear on the TV. A child's
position in that array determines the generated Up/Down offset. Nesting alone
does not prove that a route works. Anchors and transitions supply the executable
keys, and verification records the visually tested behavior. The containing
array is the only way to declare a parent; a separate `parent` field is not part
of the schema.

## Complete YAML example

```yaml
version: 1
id: s95f-1296-sdr-filmmaker-hdmi-1
name: S95F · firmware 1296
model: S95F

context:
  firmware: "1296"
  signal: SDR
  pictureMode: Filmmaker Mode
  input: HDMI 1

configurations:
  - id: default
    name: Default menu
    conditions: Game Mode = Off

timing:
  defaultDelay: 150ms
  screenChangeDelay: 800ms
  returnDelay: 300ms
  verified: true

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
    verified: true
    validationSource: settings
    returnStrategy:
      menuRoot: settings
      atMenuRoot:
        verified: true
        steps:
          - key: KEY_RETURN
      belowMenuRoot:
        verified: true
        steps:
          - key: KEY_MENU
          - key: KEY_RETURN
      overrides:
        - node: reset-picture
          verified: false
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
    verified: true
    description: Open Settings from normal video
    steps:
      - key: KEY_MENU
        delay: 1s
```

`verification` is intentionally omitted from the hand-authored example. The
app adds it after visual checks.

## Equivalent JSON example

This shorter JSON file expresses the same core concepts. JSON strings need
double quotes, arrays need commas, and comments or trailing commas are not
allowed.

```json
{
  "version": 1,
  "id": "s95f-1296-sdr-filmmaker-hdmi-1",
  "name": "S95F · firmware 1296",
  "model": "S95F",
  "context": {
    "firmware": "1296",
    "signal": "SDR",
    "pictureMode": "Filmmaker Mode",
    "input": "HDMI 1"
  },
  "configurations": [
    {
      "id": "default",
      "name": "Default menu",
      "conditions": "Game Mode = Off"
    }
  ],
  "timing": {
    "defaultDelay": "150ms",
    "screenChangeDelay": "800ms",
    "returnDelay": "300ms",
    "verified": true
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
      "verified": true,
      "validationSource": "settings",
      "returnStrategy": {
        "menuRoot": "settings",
        "atMenuRoot": {
          "verified": true,
          "steps": [{ "key": "KEY_RETURN" }]
        },
        "belowMenuRoot": {
          "verified": true,
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
      "verified": true,
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
| `context` | No | Firmware, signal, picture mode, and input for which routes were observed. |
| `verification` | No | App-maintained display and behavioral fingerprints. |
| `configurations` | No | Named alternate menu layouts. |
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
| `signal` | `any` | For example `SDR`, `HDR10`, or `HDR10+`. |
| `pictureMode` | `any` | For example `Filmmaker Mode`. |
| `input` | `any` | Input/source observed during verification. |

Use `any` only when behavior was actually verified as independent of that
dimension. The complete verification page binds the file to a recorded display
combination.

### `configurations`

Each entry requires `id` and `name`; `conditions` is optional human-readable
text. Configurations are appropriate when the menu reorders or changes in a way
that cannot be expressed by `hiddenWhen`. The application does not evaluate
`conditions` or read those values from the TV. The selected active configuration
is stored in local application settings, not inside this file.

An anchor or transition with `configuration` is eligible only when that layout
is active. Omitting `configuration` makes the behavior universal, so do that
only after verifying it in every intended layout.

## Node fields and control types

Every node requires `id` and `label`. These fields are optional:

| Field | Meaning |
| --- | --- |
| `children` | Ordered array of the rows immediately contained by a submenu. Valid only on `submenu` nodes. |
| `description` | Notes for users and future development. |
| `controlType` | Interaction type; defaults to `submenu`. |
| `defaultValue` | Expected value after reset/startup. Required for value-bearing controls; forbidden for `submenu` and `action`. |
| `minimumValue`, `maximumValue` | Numeric slider boundaries; both are required for sliders only. |
| `options` | Ordered values for choice controls. |
| `disabledWhen` | OR-list of conditions that leave this row present but gray/unavailable. Descendants inherit this state. |
| `hiddenWhen` | OR-list of conditions that remove this row and all descendants. |

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

Both conditional fields contain objects with `setting` and `equals`:

```yaml
disabledWhen:
  - setting: game-mode
    equals: on
  - setting: picture-mode
    equals: Dynamic
```

```json
"disabledWhen": [
  { "setting": "game-mode", "equals": "on" },
  { "setting": "picture-mode", "equals": "Dynamic" }
]
```

Multiple entries use **OR**, not AND: any match activates the behavior. The
referenced setting must be a slider, selection, submenu selection, indexed
selection, or switch. A condition cannot refer to its own node. Selection values
must appear in that setting's options; switch values must be `on` or `off`.

When a submenu is disabled, every descendant is also unavailable automatically.
Define `disabledWhen` only on that submenu; repeating the same condition on its
children is unnecessary. When the controlling value enables the submenu again,
its descendants become available and generated routes use the inherited state.

There is no separate “always disabled” boolean in version 1. Model a permanent
gray row through a condition that is true in the applicable configuration, or
leave it as documented topology and do not define an executable destination.
Use separate configurations if combinations require AND logic or cannot be
represented by one modeled value.

## Timing and key steps

`timing` has four fields:

| Field | Built-in default | Used after |
| --- | ---: | --- |
| `defaultDelay` | `150ms` | D-pad and other ordinary/custom keys. |
| `screenChangeDelay` | `800ms` | Menu, Enter/OK, Home, Exit, and Source. |
| `returnDelay` | `300ms` | Return. |
| `verified` | `true` for a new profile | Whether the three global values passed their dedicated visual test. |

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

## Verification manifest

After visual verification the app writes a block shaped like this in either
format:

```yaml
verification:
  display:
    model: S95F
    firmware: "1296"
    signal: SDR
    pictureMode: Filmmaker Mode
    input: HDMI 1
  checks:
    - id: display
      fingerprint: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
      verifiedAt: "2026-08-30T15:30:00.0000000+00:00"
```

`display` requires all five fields. Each check has an app-defined ID, a
64-character SHA-256 hexadecimal fingerprint, and an ISO-8601 timestamp.
Fingerprints include the display combination and the relevant behavior. Controls
sharing an interaction type are fingerprinted as one representative group.
Conditional rows are fingerprinted in disabled, hidden, or combined behavior
classes; each class fingerprint still includes every affected node and exact
`disabledWhen`/`hiddenWhen` rule. Changing only a slider's bounds, for example,
reopens the shared slider check without invalidating unrelated routes or
restoring one check for every slider.

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
- **Choice default not found:** add the exact default to `options`.
- **Invalid slider:** provide both bounds and keep the default inside them.
- **Invalid JSON:** remove comments/trailing commas and quote keys and strings.
- **Route unavailable:** verify the route for the active configuration and make
  sure conditional visibility matches SamsungController's predicted values.

For navigation confidence, generated-route behavior, and the complete guided
workflow, continue with [Menu definitions and predicted navigation](menu-model.md).
