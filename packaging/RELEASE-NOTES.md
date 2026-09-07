## SamsungController v0.3.0

This release captures the completed iteration of the menu-authoring,
display-verification, and calibration workflow, including all changes since
v0.2.0 and the updated S95F and Odyssey G9 menu definitions.

Start with the [beginner tutorial](https://github.com/whipstein/SamsungController/blob/v0.3.0/docs/getting-started.md)
for installation, first launch, pairing, verification, calibration, and upgrades.

### Highlights

- **Per-setting saved-value contexts:** use `valueContext` in YAML or JSON to
  declare which external signals or menu settings select separate current values
  and calibration targets. Rules inherit through submenus; `[]` means shared.
  Picture adjustments can follow bit depth and Picture Mode without creating
  separate RGB/YCbCr copies.
- **Conditional defaults:** `defaultValueWhen` supports both external signals
  and menu-setting values. Defaults, saved values, and visibility rules remain
  independent. The node editor exposes both default and storage-condition rules.
- **One calibration file for all contexts:** version-3 files hold shared values,
  per-mode values, and indexed 20-point/color cells. Load as current settings or
  stage as targets; these roles remain separate. A command-free context selector
  restores the appropriate local baseline without changing the TV.
- **Safer context switching:** mixed batches that change a context selector and
  its dependent settings are rejected before keys are sent. Older ambiguous
  saved values remain available for explicit review rather than being silently
  merged or discarded.
- **Independent display definitions and verification:** displays reference menu
  files without duplicating them. Verification lives only in personal sidecars;
  refresh menu files, remove mistaken confirmations, and run checks directly from
  Display Verification without modifying the distributable menu.
- **Input-aware verification:** independent HDMI color-format and bit-depth
  selectors, visible Gamma option inspections, explicit pending-test signal
  prerequisites, and corrected disabled/hidden-row positioning. Completing final
  verification opens Menu automatically.
- **Usability and navigation:** reorganized display setup, light/dark themes,
  improved contrast and control typography, top-level calibration Apply,
  remembered update behavior, corrected slider fill, and less backtracking
  between menu sections.
- **Schema diagnostics and bundled menus:** inspect a file or a directory in the
  UI or with `samsungctl menu validate`. Packages include the updated
  `s95f-1296.json` and `odyssey_g9_oled-2231-game.json` menu structures.

### Upgrade and file-format notes

- Back up your personal application-data directory and export important current
  settings/targets before upgrading. Stop the running server, extract v0.3.0 to
  a new folder, and launch it there. Do not overwrite a running installation.
- Menu schema version remains **1**, using nested `children` in YAML or JSON.
  Menu context now contains firmware only; input-specific behavior belongs in
  external states and condition rules, not the menu's context metadata.
- Existing menus without any `valueContext` declaration keep full external-state
  banking. Opt in explicitly, reviewing all branches: unspecified branches are
  shared unless they inherit a declared rule. This release does not infer a
  display's storage behavior from its visibility conditions.
- Scoped menus use **calibration version 3**. Convert older version-1/2 imports
  to explicit per-setting conditions before loading them into a scoped menu.
  Existing in-app records are retained; resolve ambiguous combinations in Menu.
  Menus without storage declarations continue to use version-2 exports.
- Bundled filenames and definition IDs are now `s95f-1296` and
  `odyssey_g9_oled-2231-game`. If a saved display still references the old name or
  a previous installation folder, update its reference to the desired menu file.
  A changed definition ID may require a new local verification/calibration
  association; the release does not silently rewrite personal references.
- SamsungController cannot read actual setting values from the TV. Confirm
  baselines before relative adjustments. Apply Picture Mode or another
  context-changing selector separately before applying its dependent controls.

See the [menu-file tutorial](https://github.com/whipstein/SamsungController/blob/v0.3.0/docs/menu-definition-file-format.md)
and [calibration-file guide](https://github.com/whipstein/SamsungController/blob/v0.3.0/docs/calibration-target-files.md)
for complete examples and migration details.

### Self-contained application packages

No repository clone or separate .NET installation is required.

Each archive contains:

- the local SamsungController web interface;
- a double-clickable platform launcher that starts the server and opens the browser;
- the optional `samsungctl` command-line interface;
- the S95F and Odyssey G9 menu structures plus a generic authoring template; and
- complete installation, pairing, use, privacy, and troubleshooting documentation.

Choose the download matching both the operating system and processor:

- `macos-arm64` for Apple silicon or `macos-x64` for Intel Macs;
- `windows-x64` for Intel/AMD Windows or `windows-arm64` for Windows on Arm; and
- `linux-x64` for Intel/AMD Linux or `linux-arm64` for Arm64/aarch64 Linux.

These packages are not code-signed. macOS Gatekeeper or Windows SmartScreen may require explicit approval. Verify that the archive came from the official `whipstein/SamsungController` GitHub release before allowing it.

After extracting the archive, open `START-HERE.md` for platform launch steps or
`docs/getting-started.md` for the full first-time workflow. Existing user data
remains in the normal per-user configuration directory when upgrading.
