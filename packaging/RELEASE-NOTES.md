## SamsungController v0.2.0

This release turns SamsungController's menu model into a complete authoring,
display-verification, and calibration workflow. It also provides a new
[beginner tutorial](https://github.com/whipstein/SamsungController/blob/v0.2.0/docs/getting-started.md)
that runs from installation through updating the application.

### Highlights

- Define portable nested TV-menu structures in YAML or JSON, including sliders,
  switches, ordered selections, submenu selections, indexed grids, actions,
  confirmations, permanently disabled rows, and conditional disabled/hidden
  branches.
- Generate navigation routes from topology and validate representative coverage
  instead of recording and approving every menu item separately.
- Keep reusable menu structures separate from local display verification, saved
  current-TV states, calibration targets, tokens, and logs.
- Verify a structure against a specific model, firmware, signal, picture mode,
  and input from one guided Display Verification workspace. Relevant structure
  edits reopen only the affected fingerprinted checks.
- Enter current settings without sending TV commands, load/save named TV states,
  stage target settings, import portable calibration targets, and cancel an
  active batch immediately when the screen and prediction diverge.
- Use compact expert, 2-point, 20-point, and custom-color controls with fixed
  grids, plus/minus controls, visual sliders, and raw values.
- Create state-aware macros with the in-app remote and verified menu calls,
  three-pass verification, optional execution confirmation, and persistent
  quick access.
- Improve cross-branch navigation, reconnect readiness, first-command warm-up,
  calculated submenu stabilization, and system timing. Left/Right value changes
  now have an independent faster default.

### File-format change

Menu topology is now nested under each submenu's children. The earlier flat
topology is intentionally not supported; convert an old personal definition
before selecting it in this release. Display verification and saved setting
values belong in local sidecar/state files rather than a distributable menu
structure.

### Self-contained application packages

No repository clone or separate .NET installation is required.

Each archive contains:

- the local SamsungController web interface;
- a double-clickable platform launcher that starts the server and opens the browser;
- the optional `samsungctl` command-line interface;
- a generic, unverified menu-definition template; and
- complete installation, pairing, use, privacy, and troubleshooting documentation.

Choose the download matching both the operating system and processor:

- `macos-arm64` for Apple silicon or `macos-x64` for Intel Macs;
- `windows-x64` for Intel/AMD Windows or `windows-arm64` for Windows on Arm; and
- `linux-x64` for Intel/AMD Linux or `linux-arm64` for Arm64/aarch64 Linux.

These packages are not code-signed. macOS Gatekeeper or Windows SmartScreen may require explicit approval. Verify that the archive came from the official `whipstein/SamsungController` GitHub release before allowing it.

After extracting the archive, open `START-HERE.md` for platform launch steps or
`docs/getting-started.md` for the full first-time workflow. Existing user data
remains in the normal per-user configuration directory when upgrading.
