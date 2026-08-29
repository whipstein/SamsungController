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

From the repository, the included example can be inspected without connecting
to a TV:

```bash
dotnet run --project src/SamsungController.Cli -- \
  macro validate --macro-file samples/macros/macros.example.yaml

dotnet run --project src/SamsungController.Cli -- \
  macro list --macro-file samples/macros/macros.example.yaml
```

Run a named macro after reviewing its keys:

```bash
dotnet run --project src/SamsungController.Cli -- \
  macro TestNavigation --macro-file samples/macros/macros.example.yaml
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
```

Supported steps are:

- `key`: any Samsung key string, with optional `action`, `repeat`, and `delay`.
- `delay`: an explicit pause such as `150ms`, `2s`, or `00:00:02`.
- `call`: invoke another macro, optionally with `repeat`.

`action` can be `Click`, `Press`, or `Release`. A delay attached to a key is
performed after every send, including the final repetition. This makes the
pause before the next step explicit and predictable.

Root variables are scalar values and can reference other variables with
`${name}`. They are resolved before actions, repeat counts, and durations are
typed. Missing variables and variable cycles are errors.

## Browser editing and behavioral verification

The web **Macros** page can create, duplicate, edit, delete, load, and download
catalogs without hand-editing YAML. The editor accepts key, delay, and nested
call steps and rewrites the complete catalog atomically only after the resulting
catalog passes the same parser and validator used by the CLI. A failed save
leaves the previous file intact. Renaming a macro updates calls to that macro.
Deletion is rejected while another macro still calls the target.

The detailed definition also accepts these optional fields:

- `verificationPasses`: an integer from `0` through `3`.
- `verified`: `true` after three accepted visual runs; `false` otherwise.

Select **Replay test** to execute the exact saved macro, then visually inspect
the TV. **Count pass** increments only that macro's stored count. **Failed**
resets only that macro to `0/3`. The service accepts a confirmation only after a
successful replay of the same macro. It does not silently send an anchor,
return-to-video sequence, or other preparation command. At `3/3`, the macro can
be pinned to quick access.

Changing a description or name preserves behavioral verification when the
operations are identical. Changing a key, action, repeat, delay, wait, or nested
call resets the macro to `0/3` and removes its quick-access entry. The visual
editor presents resolved values; saving a catalog that used `${variables}`
normalizes the steps to their concrete values while retaining the root variable
mapping for compatibility.

## Validation and execution

The complete catalog is validated before any key is sent. Validation rejects:

- missing nested macros and recursive call cycles;
- empty definitions, keys, and macro names;
- repeat counts outside 1 through 1,000;
- non-positive delays or delays over 24 hours; and
- expanded plans over 10,000 key/delay operations.

Every expanded key and delay is printed with its operation number, source macro,
and source step. Key transmissions are also captured by the normal protocol
NDJSON logger. Cancellation is checked before and during every operation. If a
macro is cancelled or fails after a `Press`, the executor makes a short,
best-effort attempt to send the matching `Release` so the key is not left held.

Macro keys intentionally are not allowlisted, matching the raw remote-key API.
Syntax validation proves that the file is structurally safe to execute; only the
three visual passes establish the application's behavioral `verified` status.
Review a macro before execution and do not automate unknown service-menu keys.
