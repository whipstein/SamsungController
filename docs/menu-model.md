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

## Version 1 YAML and JSON

Menu definitions can be stored as YAML (the default) or JSON with the same
version 1 schema and validation. See the
[menu definition file format tutorial](menu-definition-file-format.md) for the
complete raw topology/settings reference and equivalent examples.

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

# Added and maintained by the File Verification page after visual testing.
verification:
  display:
    model: S95F
    firmware: "1296"
    signal: SDR
    pictureMode: Filmmaker Mode
    input: Home Theater System
  checks:
    - id: display
      fingerprint: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
      verifiedAt: "2026-08-30T15:30:00.0000000+00:00"

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
  verified: true

nodes:
  - id: normal-video
    label: Normal video
    controlType: submenu

  - id: settings
    label: Settings
    controlType: submenu
    children:
      - id: adaptive-picture
        label: Adaptive Picture
        controlType: switch
        defaultValue: off

      - id: brightness
        label: Brightness
        controlType: slider
        defaultValue: 50
        minimumValue: 0
        maximumValue: 100
        disabledWhen:
          - setting: adaptive-picture
            equals: on
        hiddenWhen:
          - setting: picture-mode
            equals: Standard

      - id: picture-mode
        label: Picture Mode
        controlType: selection
        defaultValue: Filmmaker Mode
        options:
          - Standard
          - Movie
          - Filmmaker Mode

      - id: reset-picture
        label: Reset Picture
        controlType: confirmation
        defaultValue: Cancel
        options:
          - Reset
          - Cancel

      - id: smart-calibration
        label: Smart Calibration
        controlType: action

      - id: sound-output
        label: Sound Output
        controlType: submenu-selection
        defaultValue: TV Speaker
        options:
          - TV Speaker
          - Receiver
          - Bluetooth Speaker

      - id: interval
        label: Interval
        controlType: indexed-selection
        defaultValue: 5%
        options: [5%, 10%, 15%, 20%]

      - id: interval-red
        label: Red
        controlType: slider
        defaultValue: 0
        minimumValue: -50
        maximumValue: 50

      - id: interval-green
        label: Green
        controlType: slider
        defaultValue: 0
        minimumValue: -50
        maximumValue: 50

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

The `verification` mapping is optional until the profile is tested. Do not copy
its records between definitions or edit fingerprints by hand. The complete
verification workspace derives the required checks from the current definition and
stores a result only after visual confirmation. Each fingerprint includes the
declared display context plus the relevant route, timing, control options,
bounds, or condition. A later file edit therefore invalidates only records whose
behavior changed. The persistent header reports the file as fully verified only
when every derived check has a matching record.

`configurations` keeps alternate layouts in one TV model file. Use `hiddenWhen`
for row-presence changes driven by modeled value-bearing nodes. Use a named
configuration when rows reorder, when an unmodeled condition changes the layout,
or when a different verified route set is required. The
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
`submenu`, `slider`, `selection`, `submenu-selection`, `indexed-selection`, `switch`,
`confirmation`, or `action`; older definitions
that omit it continue to load as `submenu`. Sliders require numeric
`minimumValue` and `maximumValue` boundaries plus a numeric `defaultValue` inside
that range. Switches require `on` or `off`. A selection and a submenu selection
each require an ordered `options` list plus a `defaultValue` that matches one
option. A normal selection opens its choices and choosing a value returns to the
setting row automatically. A submenu selection opens a full value submenu;
after choosing the value, SamsungController sends `KEY_RETURN` to return to the
containing menu. An indexed selection renders each option as a fixed grid row
and treats the consecutive slider nodes immediately following it in the same
`children` array as columns. The selector is operated automatically during Apply and is
not shown as a standalone dropdown. Existing `Interval` percentage selectors
and `Color` selectors containing Red, Green, and Blue are recognized as indexed
for compatibility. A confirmation is
an action dialog rather than a persistent setting; it requires at least two
ordered `options`, and its `defaultValue` records the initially highlighted
choice. Duplicate choices are rejected. Keep lists in the same order displayed
by the TV. A submenu has no value. An `action` is a leaf row that starts a TV
function, such as Smart Calibration; generated navigation stops with that row
highlighted and deliberately omits the final OK/Enter. It has no default,
options, bounds, or children. These declarations document the expected TV
behavior; SamsungController cannot read the live value or highlighted choice.

`disabledWhen` documents rows that remain visible in their declared position
in the menu but become unavailable or gray. Each condition names another
value-bearing node and the value that disables this node. Multiple conditions
use OR behavior: any matching condition disables the row. The declared defaults
let the UI mark a row as disabled by default. A disabled submenu makes every
descendant unavailable automatically, so its children do not repeat the same
`disabledWhen` rule. Enabling the submenu restores its descendants and their
calculated routes.

`hiddenWhen` documents rows that disappear entirely. It uses the same `setting`
and `equals` entries and the same OR behavior. When a hidden rule matches, the
row and all descendants are removed from Menu Controls, the verified menu map,
and calculated sibling offsets. SamsungController orders a staged controlling
change before its dependent rows and keeps the predicted values for the current
application session. A change made with the physical remote cannot be observed;
restore the declared defaults or reproduce that change in Menu Controls before
relying on a calculated route.

Nested `children` arrays control tree presentation. Within each submenu, the
array sequence is the persistent custom order used to mirror the TV.
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

Unknown file fields, missing node references, invalid actions,
unsafe repeat counts, and excessive delays fail validation before any command
can be planned or sent.

## Observation workflow

1. Record the TV firmware, input, SDR/HDR state, and picture mode.
2. Connect in the web interface and open **Build & Verify**.
3. Follow the highlighted three-stage guide. In **Define menu**, create the TV
   profile and enter the model number and firmware version. The default
   definition name uses those two values. The generated file ID also appends
   signal type, picture mode, and input/source when their value is more specific
   than `any`. YAML is created by default; JSON can be selected in the profile
   form. The definition is stored in the per-user configuration directory and
   loaded automatically. Reusing an existing file ID opens a destructive
   replacement confirmation instead of failing silently or overwriting it
   immediately.
4. Still in **Define menu**, describe and select the settings-dependent layout that
   is currently visible. **Menu outline** automatically loads the selected file's
   branch as an indented outline in a 20-line editor with synchronized line numbers.
   Tree-aware vertical guides appear only inside populated indentation branches and
   stop when a later item returns to a shallower level. Edit it directly or use
   **Reload branch from file** to discard unsaved text.
   Add the expected menu positions with two spaces per level, choose **Preview
   changes**, and then choose **Save & keep editing** or **Save & continue** to
   advance to recording. Any text edit invalidates the preview and requires a new one. Invalid
   formatting is reported beside the editor with the affected outline line.
   Matching
   labels under the same parent reuse their stable IDs; append `[stable-id]` to a
   line when identity must be explicit. Add behavior metadata in braces before
   the stable ID, for example `Brightness {slider; default=50; min=0; max=100}`, `Picture Mode
   {selection; default=Filmmaker Mode; options=Standard|Movie|Filmmaker Mode}`,
   `Sound Output {submenu-selection; default=TV Speaker; options=TV Speaker|Receiver|Bluetooth Speaker}`,
   `Interval {indexed-selection; default=5%; options=5%|10%|15%|20%}` followed by its slider rows,
   `Adaptive Picture {switch; default=off}`, `Reset Picture {confirmation;
   default=Cancel; options=Reset|Cancel}`, or `Smart Calibration {action}`. The option order after
   `options=` is preserved. A dependent gray row can be written as `Brightness {slider;
   default=50; min=0; max=100; disabledWhen=adaptive-picture=on}`. A row that
   disappears can use `Game HDR {submenu; hiddenWhen=game-mode=off}`. Separate
   multiple conditions with `|`. The fine-adjustment editor exposes disabled and
   hidden rules, selection, submenu-selection, indexed-selection, and
   confirmation choices as an ordered add/remove/move list, and exposes slider
   boundaries as numeric fields. By default the outline synchronizes the complete
   selected branch, so deleting a line previews and saves that item as a removal.
   Enable partial-outline mode only when unlisted items should remain; recorded
   references are protected from removal in either mode. Applying the outline advances to
   **Define anchor**. Open **Fine adjustments for individual menu items** only for
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
5. In **Define anchor**, choose the base menu, normally Settings. Define the
   root return script (`KEY_RETURN` on the observed TV) and the deeper-menu
   return script (`KEY_MENU, KEY_RETURN`). Save them as one anchor definition.
   Place the TV on normal video, start the entry recorder, and capture only the
   keys that open the base menu. When this entry transition
   reaches a modeled submenu, the writer derives absolute descendant routes from
   sibling order. Each edge moves Down to the child index and presses Enter only
   when that child is a submenu; a leaf route stops on the selected row. Rows
   disabled under declared defaults are neither targeted nor counted in the
   directional offset. Explicit transitions to descendants override generated
   ones. A state-specific anchor exception remains available in the collapsed
   advanced section when one exact state cannot use the root/deeper rule.
6. Use the embedded remote. Every successfully sent button controls the TV and
   is captured; failed sends are not recorded. The system timing profile supplies
   waits unless a button has a custom override in the timing lab.
7. Stop the recording. The UI atomically adds it to the active definition, generates
   descendant routes, and opens **Verify coverage**. Generated transitions retain
   their seed and validation-group metadata in the file so they can be regenerated
   after topology edits.
8. Edit the **System-wide timing** profile, select a traversal, and
   place the TV at that traversal's source before choosing **Test system
   profile**. The test sends only the displayed traversal and ignores its
   per-button overrides. Confirm three successful visual runs to persist
   `timing.verified: true`.
9. **Verify coverage** is the complete automatically calculated checklist. It
   first displays unverified base-menu and deeper-menu return rules. Place the TV
   on normal video and choose the line item's explicit **Prepare start** action,
   or place it at the stated start manually, then run the return rule three
   times. Any unverified state-specific return exception appears as another
   calculated line item. Preparation sends only the displayed route. The checklist
   then lists one representative traversal for each affected top-level branch.
10. Open a topology-coverage card's **Timing lab**, enable custom waits only for exceptional
   button presses, then choose **Save + replay**.
   Use **Prepare source** after the deeper anchor return is verified, or place
   the TV at normal video manually. **Replay test** sends the generated route
   shown by that coverage card and retains its independent validation count.
11. Visually confirm the target after every run. Topology-generated routes expose
   only one longest representative card per top-level branch. Three confirmed
   passes verify its recorded seed and every generated route in that group; the
   card states the number promoted. A failed confirmation resets only that
   coverage card. Partial pass counts are retained independently when validation
   switches between cards. Every successful
   confirmation immediately synchronizes the
   current-menu indicator to the confirmed target, including after the third
   pass reloads the definition; that third accepted pass then runs the verified anchor
   return automatically.

For a slider, topology verification ends with the slider row highlighted. Do not
press Left or Right during that route test: the coverage result verifies only
that the generated route reaches the correct control. After the route is
verified, use **Menu Controls** to exercise the value behavior. That page
collects adjustable controls from every menu area, separates them into tabs by
top-level menu, and groups sliders, switches, selections, and submenu selections by their immediate
menu parent. It
sends one Left or Right key per whole-number slider step, Select for a switch,
and Select plus ordered Up/Down movement for a selection. A submenu selection
adds Return after choosing the value so the TV is back in the containing menu.
It then asks for
visual confirmation of the predicted value. The value confirmation is
deliberately separate from the three-pass route count because the TV does not
return its setting value. Changes made with a physical remote or another
application can invalidate the page's predicted starting value.

Menu Controls evaluates `disabledWhen` and `hiddenWhen` against predicted values.
A staged update orders controlling settings before their dependents. A
conditional child that is disabled under the declared defaults does not need to be
added to the default-state coverage checklist: after its controller enables it,
the page reaches the verified containing submenu and derives the child's Up/Down
offset from the enabled sibling order. This is intended for rows such as 20 Point
RGB controls and Custom Color Space controls that remain in the topology but
cannot be selected in the default state.

When a `hiddenWhen` rule changes, the row and descendants appear or disappear
immediately and every later sibling offset is recalculated. The ordinary verified
menu navigator uses the same in-session predictions. Default values are restored
when the definition is reloaded or the application restarts.

Slider value confirmation uses representative coverage rather than requiring
every slider independently. Each distinct slider visually confirmed on the page
adds one pass to the TV-specific shared slider profile. Three distinct sliders
promote the profile; subsequent slider updates no longer request confirmation.
Repeating one slider does not increase coverage. The profile is persisted for
the saved TV address, resets automatically when a different TV address is saved,
resets when system-wide menu delays change, and can be reset manually from Menu
Controls. Selections and submenu selections are verified individually because
each declares a different ordered option list and exit behavior. After one
successful value confirmation, that selection control's
verification is persisted for the saved TV address and later changes no longer
request routine confirmation. Selection verification resets for a different TV,
when system-wide menu delays change, or from Menu Controls. Switches are not
promoted by either coverage type and continue to request visual confirmation.

### Saved profile and factory-reset synchronization

Menu Controls stores desired values in the local application settings for the
active menu-definition ID. Indexed cells are keyed by selector, selector option,
and slider node, so all 20 Point percentages or Custom Color rows survive an
application restart. These personal target values are not written to the shared
TV topology file.

**Reset & apply all** lists verified confirmation nodes whose ID or label contains
`reset`, preferring `reset-picture`. After explicit user confirmation it
navigates to that node, opens its confirmation dialog, selects the chosen reset
action, and runs the verified return-to-video anchor. The controller then resets
its predictions to every declared `defaultValue` and applies the saved
profile in dependency order, followed by indexed grid rows in their declared
option order. The chosen confirmation determines reset scope; selecting Reset
Picture does not perform a full television ownership/device factory reset.

After initial verification, editing the outline or fine-adjustment tree runs the
same generator again. A group whose generated key sequences and membership are
unchanged retains verification. A newly added top-level branch contributes one
new coverage line item; an insertion, move, type change, or new descendant inside
an existing branch reopens only that affected branch. The verification panel
expands automatically whenever its calculated line-item count increases.

Explicit draft cards can be replayed, re-recorded under the same identifier, or
deleted entirely in the UI. Generated coverage cards are regenerated from their
seed and topology rather than manually re-recorded or deleted. Verified items are protected from this draft workflow.
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
