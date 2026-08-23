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
    steps:
      - key: KEY_RETURN
        repeat: 3
        delay: 150ms

transitions:
  - id: open-settings-overlay
    from: normal-video
    to: settings-overlay
    verified: true
    steps:
      - key: KEY_MENU
        delay: 500ms
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
  delay: 150ms        # delay after each send; ms or s
```

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
   uses a verified anchor and verified routes to position the TV first.
5. Use the embedded remote. Every successfully sent button controls the TV and
   is captured; failed sends are not recorded.
6. Stop the recording. The UI atomically adds it to the active YAML as a draft.
7. Under **Replay and validate drafts**, run the draft. Transition validation
   first executes a verified anchor and verified route to its source, then sends
   the recorded buttons.
8. Visually confirm the target after every run. Three confirmed passes update
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
