# Macro engine

Macros are parsed and validated by the cross-platform
`SamsungController.Automation` library. Serialization, validation, planning,
and execution are separate concerns; neither the macro engine nor its models
depend on the CLI.

## File location and commands

The initial default is `macros.yaml` in the same per-user configuration
directory used for settings, tokens, and session logs. Use `--macro-file <path>`
to load a different file. After that file is successfully validated, listed, or
used by a macro/console command, its absolute path is remembered for subsequent
invocations. This means a later `console` command uses the same catalog without
requiring the flag again.

In a source checkout, keep a real TV's catalog under ignored `user-data/` (for
example, `user-data/macros.yaml`). The tracked
`samples/macros/macros.example.yaml` file is a generic, unverified format
example and must not be used as the live catalog.

Copy the included example to `user-data/macros.yaml` before using these
commands. The ignored copy can be inspected without connecting to a TV and is
safe to make your remembered working catalog:

```bash
dotnet run --project src/SamsungController.Cli -- \
  macro validate --macro-file user-data/macros.yaml

dotnet run --project src/SamsungController.Cli -- \
  macro list --macro-file user-data/macros.yaml
```

Run a named macro after reviewing its keys:

```bash
dotnet run --project src/SamsungController.Cli -- \
  macro ExampleSequence --macro-file user-data/macros.yaml
```

The interactive console accepts the same `macro list`, `macro validate`,
`macro <name>`, and `macro run <name>` forms. Ctrl+C cancels a running macro.

## YAML format

Both a compact step sequence and a detailed definition with `description` and
`steps` are supported:

```yaml
version: 1

variables:
  direction: KEY_DOWN
  repeats: 4
  navigationDelay: 150ms

macros:
  BackToVideo:
    - key: KEY_RETURN
      repeat: 5
      delay: ${navigationDelay}

  MoveAndSelect:
    description: Demonstrates variables, nesting, and an explicit delay
    start: normal-video
    confirmBeforeRun: true
    verified: false
    verificationPasses: 1
    steps:
      - call: BackToVideo
      - key: ${direction}
        repeat: ${repeats}
        delay: ${navigationDelay}
      - delay: 500ms
      - key: KEY_ENTER
        action: Click
      - menu: picture-brightness
```

Supported steps are:

- `key`: any Samsung key string, with optional `action`, `repeat`, and `delay`.
- `delay`: an explicit pause such as `150ms`, `2s`, or `00:00:02`.
- `call`: invoke another macro, optionally with `repeat`.
- `menu`: navigate to a verified menu node ID through the web application's current-state planner.

`action` can be `Click`, `Press`, or `Release`. A delay attached to a key is
performed after every send, including the final repetition. This makes the
pause before the next step explicit and predictable.

`start` declares the verified menu node where a detailed macro begins. It is
optional for backward compatibility with hand-authored and CLI-only catalogs,
but the browser editor requires it for new or edited macros. The value is a
stable node ID from the active menu definition, including states such as
`normal-video`.

`confirmBeforeRun` is optional and defaults to `false`. When it is `true`, the
web interface asks **Are you sure?** before a Replay or persistent quick-access
run sends any commands. It is interactive UI safety metadata; terminal CLI and
console execution remain non-interactive, so review macros before running them
there.

Root variables are scalar values and can reference other variables with
`${name}`. They are resolved before actions, repeat counts, and durations are
typed. Missing variables and variable cycles are errors.

## Browser editing and behavioral verification

The web **Macros** page can create, duplicate, edit, delete, load, and download
catalogs without hand-editing YAML. Catalogs open with no default macro
selection, and selecting the active macro card again clears its selection and
details. Its compact remote sends keys to the TV and appends a key step only
after a successful send. The adjacent verified-menu list executes a selected
destination immediately and appends one `menu` step after successful
navigation. **Pause capture** leaves both surfaces live while suppressing new
draft steps until capture resumes. **Prepare selected start** moves
the TV to the declared start through verified navigation before live capture;
those preparation keys are not appended because replay derives them from the
saved `start`. A configurable captured-key
wait is stored as that key's explicit `delay`; undo and clear controls edit the
draft without sending compensating commands. The editor accepts key, delay,
nested call, and verified menu-destination steps and rewrites the complete
catalog atomically only after the resulting
catalog passes the same parser and validator used by the CLI. A failed save
leaves the previous file intact. Renaming a macro updates calls to that macro.
Each catalog row has an **×** delete control followed by an inline confirmation.
Deletion is rejected while another macro still calls the target and when it
would leave the catalog empty. The rejection names every direct caller and its
call-step number so those references can be removed or replaced in the editor
before retrying the deletion; the catalog is left unchanged.

The detailed definition also accepts these fields:

- `start`: the verified starting menu-state node ID.
- `confirmBeforeRun`: whether the web UI must confirm a root Replay or quick-access run; defaults to `false`.
- `verificationPasses`: an integer from `0` through `3`.
- `verified`: `true` after three accepted visual runs; `false` otherwise.

Select **Replay test** to execute the saved macro, then visually inspect the TV.
The declared `start` is operation 1 in the visible execution log, not a hidden
reset. It asks the verified menu planner to reach that node from the expected
current state. If the state is unknown, a verified anchor establishes it first;
if it already matches, no preparation key is sent. **Count pass** increments
only that macro's stored count. **Failed** resets only that macro to `0/3`. The
service accepts a confirmation only after a successful replay of the same
macro. At `3/3`, the macro can be pinned to quick access.

Changing a description, name, or confirmation preference preserves behavioral verification when the
starting state and operations are identical. Changing the start, a key, action,
repeat, delay, wait, or nested call resets the macro to `0/3` and removes its
quick-access entry. Each called macro prepares its own declared start before its
steps, including on repeated calls. The visual
editor presents resolved values; saving a catalog that used `${variables}`
normalizes the steps to their concrete values while retaining the root variable
mapping for compatibility.

### Menu destination calls

`menu: <node-id>` is an explicit high-level operation. The web executor
preflights every referenced node before sending the macro's first key. The node
must exist in the active menu definition and be exposed as a verified
destination on the ordinary **Menu** page. At that operation, SamsungController
plans and executes the same shortest verified route from the expected current
menu state. The call may therefore use a verified composite or anchor route when
the planner selects one; it does not copy a fixed traversal into the macro.

If the expected state is already unknown and no declared start occurs before a
menu call, preflight rejects the run before its first key. A declared start can
recover through a verified anchor. If a later explicit operation makes the
expected state unknown, a menu call fails at that point unless another declared
nested-macro start reestablishes it. The terminal CLI has no menu-definition or
predicted-state context, so it rejects a macro containing `start` or `menu`
before sending any key; run those macros from the web interface.

## Validation and execution

The complete catalog is validated before any key is sent. Validation rejects:

- missing nested macros and recursive call cycles;
- empty definitions, keys, starting states, menu destinations, and macro names;
- repeat counts outside 1 through 1,000;
- non-positive delays or delays over 24 hours; and
- expanded plans over 10,000 key, delay, or menu operations.

Every expanded operation is printed with its operation number, source macro,
and source step. Key transmissions are also captured by the normal protocol
NDJSON logger. Cancellation is checked before and during every operation. If a
macro is cancelled or fails after a `Press`, the executor makes a short,
best-effort attempt to send the matching `Release` so the key is not left held.

Macro keys intentionally are not allowlisted, matching the raw remote-key API.
Syntax validation proves that the file is structurally safe to execute; only the
three visual passes establish the application's behavioral `verified` status.
Review a macro before execution and do not automate unknown service-menu keys.
