# Menu definitions and predicted navigation

SamsungController cannot read the current Samsung OSD cursor position. The menu
navigator therefore maintains an explicitly predicted state. It never presents
that prediction as TV-reported truth.

## Safety model

A menu definition contains three separate concepts:

- **Nodes** organize known or hypothesized TV menu locations.
- **Transitions** describe a directed key sequence from one node to another.
- **Anchors** perform a deterministic sequence intended to re-establish a known
  node from an uncertain state.

Every transition and anchor is either `verified: true` or draft. Draft routes
exist only in **Build & Verify**, where they can be replayed for visual testing;
the everyday **Menu** page displays and plans verified items only. Mark an item
verified only after recording the exact TV context and repeatable observed
result in [research-notes.md](research-notes.md).

## Confidence

Predicted state uses four levels:

| Confidence | Meaning |
| --- | --- |
| `Unknown` | No usable current node is known. Run a verified anchor. |
| `Low` | A node was known, but disconnect/time or another uncertainty reduced trust. |
| `Probable` | A verified transition completed, but the TV did not acknowledge OSD position. |
| `Synchronized` | A verified deterministic anchor just completed. |

An unmodeled menu-affecting key, raw request, partial failure, or cancelled plan
marks state unknown. Volume and other keys that do not normally affect menu
position preserve the prediction. Manual keys and macro steps pass through the
same tracker as the Menu Navigator.

The web header exposes the predicted node's short label in large type on every
page. The full path and confidence remain available as hover/accessibility
context. This is where SamsungController expects the TV menu to be, not visual
or protocol feedback from the TV. The default **Return to video** quick-access
button runs the verified `normal-video` anchor and, on success, restores a
Synchronized prediction.

The web connection workflow also runs the preferred verified anchor once after
the control channel is authorized. It prioritizes a target named
`normal-video`, then falls back to another verified anchor. No additional anchor
or reset commands are inserted before later navigation or validation runs.

## Version 1 YAML

The bundled, deliberately unverified template is
[`samples/menus/menu.example.yaml`](../samples/menus/menu.example.yaml). Its basic
shape is:

```yaml
version: 1
id: generic-picture-menu
name: Generic Samsung Picture Menu Template
model: Replace with your TV model

context:
  firmware: unrecorded
  signal: unrecorded
  pictureMode: unrecorded
  input: unrecorded

configurations:
  - id: standard
    name: Standard menu
    conditions: Game Mode = Off
  - id: game-mode
    name: Game Mode menu
    conditions: Game Mode = On

timing:
  defaultDelay: 300ms
  screenChangeDelay: 800ms
  returnDelay: 300ms
  verified: false

nodes:
  - id: normal-video
    label: Normal video
    controlType: submenu

  - id: settings
    label: Settings
    controlType: submenu

  - id: adaptive-picture
    label: Adaptive Picture
    parent: settings
    controlType: switch
    defaultValue: off

  - id: brightness
    label: Brightness
    parent: settings
    controlType: slider
    defaultValue: 50
    minimumValue: 0
    maximumValue: 100
    disabledWhen:
      - setting: adaptive-picture
        equals: on

  - id: picture-mode
    label: Picture Mode
    parent: settings
    controlType: selection
    defaultValue: Filmmaker Mode
    options:
      - Standard
      - Movie
      - Filmmaker Mode

  - id: reset-picture
    label: Reset Picture
    parent: settings
    controlType: confirmation
    defaultValue: Cancel
    options:
      - Reset
      - Cancel

anchors:
  - id: normal-video
    label: Return to normal video
    target: normal-video
    configuration: standard
    verified: false
    returnStrategy:
      menuRoot: settings
      atMenuRoot:
        verified: false
        steps:
          - key: KEY_RETURN
      belowMenuRoot:
        verified: false
        steps:
          - key: KEY_RETURN
    steps:
      - key: KEY_RETURN

transitions:
  - id: open-settings
    from: normal-video
    to: settings
    configuration: standard
    verified: false
    steps:
      - key: KEY_MENU
```

`configurations` keeps settings-dependent visible layouts in one TV model file.
Use one entry for each combination that changes row presence or order. The
`conditions` value is a human-readable checklist, not an expression evaluated
from TV telemetry: Samsung does not report the highlighted row or these setting
values over this control channel. The active configuration is selected in the
persistent header or Build & Verify and is stored in per-user application
settings. Changing it deliberately resets expected menu position to unknown.

An anchor or transition with `configuration` is eligible only while that
configuration is active. An entry without `configuration` is universal and may
be used in every layout, so omit it only for behavior that has genuinely been
verified as layout-independent. New UI recordings are automatically tagged with
the active configuration. A verified route from another configuration never
contributes to direct or calculated navigation.

Each node also describes how the highlighted row behaves. `controlType` is
`submenu`, `slider`, `selection`, `switch`, or `confirmation`; older definitions
that omit it continue to load as `submenu`. Sliders require numeric
`minimumValue` and `maximumValue` boundaries plus a numeric `defaultValue` inside
that range. Switches require `on` or `off`. A selection requires an ordered
`options` list plus a `defaultValue` that matches one option. A confirmation is
an action dialog rather than a persistent setting; it requires at least two
ordered `options`, and its `defaultValue` records the initially highlighted
choice. Duplicate choices are rejected. Keep lists in the same order displayed
by the TV. A submenu has no value. These declarations document the expected TV
behavior; SamsungController cannot read the live value or highlighted choice.

`disabledWhen` documents rows that remain visible and occupy their normal place
in the menu but become unavailable or gray. Each condition names another
value-bearing node and the value that disables this node. Multiple conditions
use OR behavior: any matching condition disables the row. The declared defaults
let the UI mark a row as disabled by default; changing a value on the TV does not
automatically update that prediction. Disabled metadata never removes a node or
rewrites its recorded key sequence, so traversal order continues to include a
gray row unless a separately selected menu configuration describes a topology
where that row is actually absent.

Parent relationships on nodes control tree presentation only. Within each parent,
the YAML node sequence is the persistent custom order used to mirror the TV.
Build & Verify can move siblings up or down and can temporarily display every
branch alphabetically without changing that saved custom order. Tree order does
not imply that navigation is possible; the verified Menu page follows the saved
custom order. The planner uses only explicit directed
transitions, adds a strong penalty to draft edges during preview, and normally
prefers a longer verified route over a short draft route.

Supported key-step fields are:

```yaml
- key: KEY_RIGHT
  action: Click       # Click, Press, or Release
  repeat: 2           # 1..100
  delay: 250ms        # optional override of system timing; ms or s
```

The `timing` profile is system-wide for the menu definition. `defaultDelay`
applies to D-pad and custom keys, `screenChangeDelay` applies to Menu, OK,
Home, Exit, and Source, and `returnDelay` applies to Return. A step-level
`delay` is an explicit per-button override. `verified` records whether the
current three global values passed the system profile's independent three-run
visual test. Changing any global value clears it.

A transition can optionally contain `returnSteps` immediately before its normal
`steps`. These return keys describe how that transition's target returns to the
normal-video anchor. They are recorded as phase two of the same traversal and
share the transition's `verified` state and three-pass count.

A normal-video anchor can optionally define `returnStrategy`. A verified entry
in `overrides` takes priority when the predicted position exactly matches its
`node`. Otherwise, the navigator uses `atMenuRoot` at the configured root and
`belowMenuRoot` at a descendant. Each script must pass its own three-run visual
test before normal navigation can use it. If position confidence is too low, an
exact override is absent or unverified, or an older definition has no return
strategy, resolution falls through the root/deeper scripts and ultimately the
anchor's ordinary `steps` fallback.

Unknown YAML fields, missing node references, parent cycles, invalid actions,
unsafe repeat counts, and excessive delays fail validation before any command
can be planned or sent.

## Observation workflow

1. Record the TV firmware, input, SDR/HDR state, and picture mode.
2. Connect in the web interface and open **Build & Verify**.
3. Follow the highlighted five-step guide. If no definition exists, start with
   **TV profile** and enter the model number and firmware version. The default
   definition name uses those two values. The generated file ID also appends
   signal type, picture mode, and input/source when their value is more specific
   than `any`. The YAML is stored in the per-user configuration directory and
   loaded automatically. Reusing an existing file ID opens a destructive
   replacement confirmation instead of failing silently or overwriting it
   immediately.
4. In **Configuration**, describe and select the settings-dependent layout that
   is currently visible. **Menu outline** automatically loads the selected YAML
   branch as an indented outline in a 20-line editor with synchronized line numbers;
   edit it directly or use **Reload branch from YAML** to discard unsaved text.
   Add the expected menu positions with two spaces per level, choose **Preview
   changes**, and then choose **Save & keep editing** or **Save & continue** to
   advance to recording. Any text edit invalidates the preview and requires a new one. Invalid
   formatting is reported beside the editor with the affected outline line.
   Matching
   labels under the same parent reuse their stable IDs; append `[stable-id]` to a
   line when identity must be explicit. Add behavior metadata in braces before
   the stable ID, for example `Brightness {slider; default=50; min=0; max=100}`, `Picture Mode
   {selection; default=Filmmaker Mode; options=Standard|Movie|Filmmaker Mode}`,
   `Adaptive Picture {switch; default=off}`, or `Reset Picture {confirmation;
   default=Cancel; options=Reset|Cancel}`. The option order after
   `options=` is preserved. A dependent gray row can be written as `Brightness {slider;
   default=50; min=0; max=100; disabledWhen=adaptive-picture=on}`; separate multiple disabling
   conditions with `|`. The fine-adjustment editor exposes selection and
   confirmation choices as an ordered add/remove/move list, and exposes slider
   boundaries as numeric fields. By default the outline synchronizes the complete
   selected branch, so deleting a line previews and saves that item as a removal.
   Enable partial-outline mode only when unlisted items should remain; recorded
   references are protected from removal in either mode. Applying the outline advances to
   **Record route**. Open **Fine adjustments for individual menu items** only for
   precise corrections and descriptions. Choose **Custom · TV
   order** and move siblings up or down to match the on-screen menu; switch to
   **Alphabetical** when that view is more useful. Display names and parents remain
   editable; stable node IDs do not change after creation. Tree labels are red
   when no command has been recorded, yellow when a recorded command still needs
   verification, and the standard color once verified.
   Node creation shares the editor with updates. **Add node** at the top opens a
   blank form while retaining the displayed parent-menu choice; **Save** creates
   it, **Cancel** restores the previously selected node, and **Update** changes an
   existing node. With focus in the tree, Up/Down select adjacent visible
   positions, Left selects the parent, and Right selects the first child without
   scrolling the page.
   When selections change which rows are present, add a named menu configuration
   for each visible layout and record its relevant selection values. Choose the
   matching configuration before recording, validation, or everyday navigation.
   Nodes may exist only as topology documentation; only destinations with a
   route verified in the active configuration appear on the ordinary Menu page.
5. Choose **Anchor** or **Transition**, select two predefined nodes, and start
   live recording. For a transition, **Prepare source**
   uses a verified anchor and verified routes to position the TV first. The UI
   derives the transition's internal YAML identifier from its target control;
   selecting the same source and target again automatically replaces that draft.
   To define a target-specific return, check **Also record return to normal
   video**. Record the forward traversal first, choose **Continue · record
   return**, and then record the return keys as phase two of the same draft.
6. Use the embedded remote. Every successfully sent button controls the TV and
   is captured; failed sends are not recorded. The system timing profile supplies
   waits unless a button has a custom override in the timing lab.
7. Stop the recording. The UI atomically adds it to the active YAML as a draft.
8. Edit the **System-wide timing** profile, select a traversal, and
   place the TV at that traversal's source before choosing **Test system
   profile**. The test sends only the displayed traversal and ignores its
   per-button overrides. Confirm three successful visual runs to persist
   `timing.verified: true`.
9. In **Return to normal video scripts**, edit the Settings-root and deeper-menu
   key sequences independently. Place the TV at the named starting position,
   then choose **Save + test**. It sends only the proposed script and asks for
   visual confirmation. Add an exact-state override when one menu position needs
   different keys; **Prepare start** is an explicit, separate positioning action.
   Three successful runs mark each script verified in the YAML.
10. Open the draft's **Timing lab**, enable custom waits only for exceptional
   button presses, then choose **Save + replay**.
   Use **Prepare source** when desired, or place the TV at the displayed source
   manually. For a traversal with integrated return keys, Prepare source sends
   those keys first and then follows any verified route to the forward source;
   if preparation is wrong or incomplete, adjust the TV manually. **Replay
   test** sends the forward keys. Both operations stay on the same draft card
   and retain one validation count.
11. Visually confirm the target after every run. Three confirmed passes update
   the same YAML entry to `verified: true`; a failed confirmation resets the
   count for that item to zero. Partial pass counts are retained independently
   when validation switches between draft commands. Every successful
   confirmation immediately synchronizes the
   current-menu indicator to the confirmed target, including after the third
   pass reloads the YAML; that third accepted pass then runs Return to video
   automatically. While that exact target remains synchronized, the
   normal-video anchor may use its recorded return even before the traversal
   reaches 3/3; otherwise draft return keys remain unavailable.

Draft cards can be replayed, re-recorded under the same identifier, or deleted
entirely in the UI. Verified items are protected from this draft workflow.
The separate **Manage verified settings** area can remove an incorrect verified
setting after a second confirmation. Its descendants and all related routes are
removed with it; verified anchor targets are protected from deletion.
Build & Verify sections can be minimized. If a minimized return script or system
timing profile changes from verified to needing validation, it opens automatically;
the validation section likewise opens when a new draft appears.

On the ordinary **Menu** page, clicking a verified destination immediately plans
and executes the verified route from the predicted current state. Planning fails
without sending commands when the current state is unknown or no verified path
exists. The route-preview controls remain available for inspecting commands.

Validate an anchor before transitions that depend on it, and validate a parent
transition before a deeper transition whose source is reached through that
parent. The ordinary navigator continues to refuse draft execution outside this
explicit authoring workflow.

Do not map service-menu entries or undocumented writes through this normal-menu
navigator.
