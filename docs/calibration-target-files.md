# Calibration target files

SamsungController uses `.samsung-calibration.json` files for portable menu
values. A calibration file is not a menu definition. The values can be used in
either of two explicitly selected roles:

- the shared YAML or JSON menu definition describes where controls exist and
  how to reach them;
- a current-TV state is the baseline SamsungController should use for relative
  moves;
- under **Adjust TV**, the file describes the values you want to reach;
- under **Enter current settings**, the file describes values you assert are
  already present on the TV.

Both loading paths validate the file and send no TV command. **Adjust TV**
stages the values without replacing the current-TV baseline. **Enter current
settings** fills its command-free draft; review it and select **Save entered
values** before it becomes the prediction baseline. Only use that second path
when the display already matches the file.

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
may contain only selected values. Loading it under Adjust TV merges those values
with the other desired values and saves the resulting local target profile.
Loading it under Enter current settings merges those values into the current
draft, leaving unspecified controls at their existing baseline. Files are
limited to 2 MB and 5,000 values. Invalid JSON errors include a one-based line
and column.

## Safe application

Large Apply and reset/apply operations show a red sticky progress strip. Select
**Cancel active update** as soon as the visible TV state differs from the phase
shown. Cancellation prevents the next key after an already in-flight send or
wait, and deliberately sends no automatic cleanup commands. Inspect the TV and
record a new current-TV baseline before retrying a partly completed operation.
