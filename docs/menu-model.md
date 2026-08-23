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
can be displayed and planned for research, but the executor refuses to send
them. Mark an item verified only after recording the exact TV context and
repeatable observed result in [research-notes.md](research-notes.md).

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

The web header exposes this predicted path and confidence on every page. It is
a statement of where SamsungController expects the TV menu to be, not visual or
protocol feedback from the TV. The default **Return to video** quick-access
button runs the verified `normal-video` anchor and, on success, restores a
Synchronized prediction.

## Version 1 YAML

The initial S95F definition is
[`samples/menus/s95f-draft.yaml`](../samples/menus/s95f-draft.yaml). Its basic
shape is:

```yaml
version: 1
id: s95f-picture-draft
name: S95F Picture Menu Draft
model: Samsung S95F

context:
  firmware: unrecorded
  signal: any
  pictureMode: any
  input: any

timing:
  defaultDelay: 150ms
  screenChangeDelay: 500ms
  returnDelay: 300ms
  verified: false

nodes:
  - id: normal-video
    label: Normal video

  - id: settings-overlay
    label: Settings overlay

anchors:
  - id: normal-video
    label: Return to normal video
    target: normal-video
    verified: true
    returnStrategy:
      menuRoot: settings-overlay
      atMenuRoot:
        verified: true
        steps:
          - key: KEY_RETURN
      belowMenuRoot:
        verified: false
        steps:
          - key: KEY_MENU
          - key: KEY_RETURN
    steps:
      - key: KEY_MENU
        repeat: 2

transitions:
  - id: open-settings-overlay
    from: normal-video
    to: settings-overlay
    verified: true
    steps:
      - key: KEY_MENU
```

Parent relationships on nodes control tree presentation only. They do not imply
that navigation is possible. The planner uses only explicit directed
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

A normal-video anchor can optionally define `returnStrategy`. When the predicted
position is the configured `menuRoot`, the navigator uses `atMenuRoot`; when it
is a descendant, it uses `belowMenuRoot`. Each script must pass its own three-run
visual test before normal navigation can use it. If position confidence is too
low, the applicable script is unverified, or an older definition has no return
strategy, the anchor's ordinary `steps` remain the deterministic fallback.

Unknown YAML fields, missing node references, parent cycles, invalid actions,
unsafe repeat counts, and excessive delays fail validation before any command
can be planned or sent.

## Observation workflow

1. Record the TV firmware, input, SDR/HDR state, and picture mode.
2. Connect in the web interface and open **Menu**.
3. Open **Menu Authoring Studio**. Create a new TV interface there if no
   definition exists; the generated YAML is stored in the per-user configuration
   directory and loaded automatically.
4. Choose **Anchor** or **Transition**, select existing nodes or describe a new
   target node, and start live recording. For a transition, **Prepare source**
   uses a verified anchor and verified routes to position the TV first. The UI
   derives the transition's internal YAML identifier from its target control;
   selecting the same source and target again automatically replaces that draft.
5. Use the embedded remote. Every successfully sent button controls the TV and
   is captured; failed sends are not recorded. The system timing profile supplies
   waits unless a button has a custom override in the timing lab.
6. Stop the recording. The UI atomically adds it to the active YAML as a draft.
7. Edit the **System-wide timing** profile, select a traversal, and
   place the TV at that traversal's source before choosing **Test system
   profile**. The test sends only the displayed traversal and ignores its
   per-button overrides. Confirm three successful visual runs to persist
   `timing.verified: true`.
8. In **Return to normal video scripts**, edit the Settings-root and deeper-menu
   key sequences independently. Place the TV at the named starting position,
   then choose **Save + test**. It sends only the proposed script and asks for
   visual confirmation. Three successful runs mark that script verified in the
   YAML.
9. Open the draft's **Timing lab**, enable custom waits only for exceptional
   button presses, then choose **Save + replay**.
   First place the TV at the source displayed on the draft card. Validation
   sends only the recorded buttons; no anchor or route-to-source commands are
   added implicitly.
10. Visually confirm the target after every run. Three confirmed passes update
   the same YAML entry to `verified: true`; a failed confirmation resets the
   count to zero.

Draft cards can be replayed, re-recorded under the same identifier, or deleted
entirely in the UI. Verified items are protected from this draft workflow.

Validate an anchor before transitions that depend on it, and validate a parent
transition before a deeper transition whose source is reached through that
parent. The ordinary navigator continues to refuse draft execution outside this
explicit authoring workflow.

Do not map service-menu entries or undocumented writes through this normal-menu
navigator.
