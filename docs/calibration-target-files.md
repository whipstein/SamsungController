# Calibration target files

SamsungController uses `.samsung-calibration.json` files for portable desired
menu values. A target file is not a menu definition and is not a snapshot of
what the TV currently contains:

- the shared YAML or JSON menu definition describes where controls exist and
  how to reach them;
- a current-TV state is the baseline SamsungController should use for relative
  moves;
- a calibration target file describes the values you want to reach.

Loading a target file validates and stages its values. It does not send a TV
command and does not replace the current-TV baseline. Select **Apply** only
after loading or entering a baseline that matches the display, or use the
explicitly confirmed reset/apply workflow to start from declared defaults.

## Format

```json
{
  "version": 1,
  "name": "Reference SDR calibration",
  "definitionId": "s95f-1296-sdr",
  "definitionName": "S95F 1296 SDR",
  "model": "S95F",
  "context": {
    "firmware": "1296",
    "signal": "SDR",
    "pictureMode": "Filmmaker Mode",
    "input": "Home Theater System"
  },
  "exportedAtUtc": "2026-08-30T12:00:00Z",
  "values": [
    {
      "nodeId": "brightness",
      "value": "25"
    },
    {
      "nodeId": "white-balance-20-point-red",
      "value": "2",
      "selectorNodeId": "white-balance-20-point-interval",
      "selectorValue": "5%"
    }
  ]
}
```

`version`, `name`, `definitionId`, and at least one `values` entry are required.
The active menu definition ID must match `definitionId`. Ordinary controls use
`nodeId` and `value`. A fixed indexed-grid cell also supplies both
`selectorNodeId` and `selectorValue`; supplying only one is invalid. IDs and
values must exist in the active definition, slider values must be whole numbers
inside their declared boundaries, and duplicate ordinary or indexed keys are
rejected.

The Menu page exports every desired value currently shown. A hand-authored file
may contain only selected values; loading it merges those targets with the
other desired values already in the page, then saves the resulting local target
profile. Files are limited to 2 MB and 5,000 values. Invalid JSON errors include
a one-based line and column.

## Safe application

Large Apply and reset/apply operations show a red sticky progress strip. Select
**Cancel active update** as soon as the visible TV state differs from the phase
shown. Cancellation prevents the next key after an already in-flight send or
wait, and deliberately sends no automatic cleanup commands. Inspect the TV and
record a new current-TV baseline before retrying a partly completed operation.
