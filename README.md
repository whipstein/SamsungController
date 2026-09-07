# SamsungController

SamsungController is a local, cross-platform controller for modern Samsung/Tizen TVs. It can pair with a TV over the LAN, send remote-control keys, run repeatable YAML macros, adjust visually verified menu controls, and capture Samsung WebSocket traffic for troubleshooting or protocol research.

The web interface is the recommended way to use the application. A command-line interface (`samsungctl`) is also included for scripting, terminal use, and diagnostics.

**v1 preview branch:** `feature/ip-remote-v1` starts the next major version at `1.0.0-alpha.1`. Use **IP Remote · Preview** for HTTPS pairing and separate **Contrast, Color, and Sharpness** change/readback/visual-check/restore tests. Once verified for your display/context, open **Direct picture controls** for all three together: refresh current TV values, stage edits, Apply with per-setting progress, or Stop. JSON presets load as pending targets only. Completed changes stay on the TV; **Stage previous values** prepares an explicit reversal. Verification and recovery remain private and independent of presets. **IP Commands** adds explicit testing of the 24 documented method families (modes, sound, inputs, channels, keys, apps, power and additional picture fields); see the [command-testing and allowed-range guide](docs/ip-remote-commands.md). Unknown advanced calibration methods remain unavailable. Existing remote/menu controls are unchanged. Follow the [IP Remote setup and verification guide](docs/ip-remote-preview.md), especially the [combined-workspace tutorial and batch recovery rules](docs/ip-remote-preview.md#9-use-the-combined-direct-picture-workspace). The stable release remains v0.3.0; this branch will not be merged into `main` until explicitly approved.

## Features

- Secure (`wss://`, normally port 8002) and non-secure (`ws://`, normally port 8001) Samsung connections
- First-use TV authorization with host-specific token storage
- Remote keys using `Click`, `Press`, and `Release` actions
- Browser-edited YAML macros with variables, nested calls, explicit waits, progress, and three-pass visual verification
- Saved display definitions that bind connection settings to one or more reusable menu definitions without changing verification
- Distributable YAML/JSON menu structures with local display-verification sidecars and a guided menu-map builder
- Compact menu-wide controls, fixed 20 Point/Custom Color grids, named current-TV states, dual-purpose portable calibration files, and cancellable batch updates
- Persistent quick-access buttons and an expected-current-menu indicator
- Searchable RX/TX protocol messages, redacted browser views, and NDJSON session logs
- Interactive terminal console with persistent command history
- Self-contained macOS, Windows, and Linux downloads for x64 and Arm64 computers

Pairing and basic remote keys may work with many recent Samsung/Tizen TVs, but menu layouts, key behavior, available queries, and authorization details vary by model and firmware. Bundled model-specific menu structures are reference starting points; SamsungController still requires local display verification for the selected model and firmware before the Menu controls unlock.

## Before you install

Both installation methods require:

1. A computer and Samsung TV on the same trusted local network.
2. The TV's IPv4 address. SamsungController does not currently discover TVs automatically. A DHCP reservation is recommended so the address does not change.
3. A current web browser for the local web interface.

The downloadable release is recommended for most users. It is self-contained and does not require Git, a repository clone, the .NET SDK, or a separate .NET runtime. Building from source remains available for contributors and users who want to modify the code.

**New to SamsungController?** Follow the [complete beginner guide](docs/getting-started.md). It walks through choosing a download, launching the app, pairing, menu setup and verification, recording current values, updating TV settings, macros, upgrades, and troubleshooting in one continuous tutorial.

## Install a downloadable release

1. Open the [latest SamsungController release](https://github.com/whipstein/SamsungController/releases/latest).
2. Download the archive matching both the operating system and processor.
3. Extract the complete archive. Do not move individual binaries out of the extracted `SamsungController` folder.
4. Run the included platform launcher. It starts the local server, waits for it to become ready, and opens [http://127.0.0.1:5050](http://127.0.0.1:5050).

Each release also provides `SHA256SUMS.txt` for optional download-integrity verification.

| Computer | Download name |
| --- | --- |
| Apple silicon Mac | `SamsungController-*-macos-arm64.tar.gz` |
| Intel Mac | `SamsungController-*-macos-x64.tar.gz` |
| Intel/AMD Windows PC | `SamsungController-*-windows-x64.zip` |
| Windows on Arm | `SamsungController-*-windows-arm64.zip` |
| Intel/AMD Linux PC | `SamsungController-*-linux-x64.tar.gz` |
| Arm64/aarch64 Linux | `SamsungController-*-linux-arm64.tar.gz` |

### Start the macOS package

1. Double-click the `.tar.gz` download to extract it.
2. Open the extracted `SamsungController` folder.
3. Control-click **Start SamsungController.command**, choose **Open**, and confirm **Open**. Keep the Terminal window open while using SamsungController.

The initial packages are not signed or notarized. If macOS blocks the application, open **System Settings > Privacy & Security** and choose **Open Anyway** for SamsungController. If it continues to block an embedded file, the terminal fallback is:

```bash
xattr -dr com.apple.quarantine /path/to/SamsungController
```

Only use that override for an archive downloaded from the official GitHub release.

### Start the Windows package

1. Right-click the `.zip` download and select **Extract All**. Do not run the launcher inside the ZIP preview.
2. Open the extracted `SamsungController` folder.
3. Double-click **Start SamsungController.cmd** and keep its command window open.

The initial packages are unsigned. If Microsoft Defender SmartScreen appears, choose **More info > Run anyway** only after confirming the archive came from the official GitHub release.

### Start the Linux package

1. Extract the `.tar.gz` download and open the resulting `SamsungController` folder.
2. Double-click **start-samsungcontroller.sh** and choose **Run**.
3. If the file manager does not offer Run, open **Properties > Permissions** and enable execution. The terminal fallback is `./start-samsungcontroller.sh`.

Linux automatic browser opening uses `xdg-open` or `gio`. If neither is installed, paste `http://127.0.0.1:5050` into a browser on the same computer.

### Stop or update a packaged release

Keep the launcher window or process running while using the site. Close that window or press Ctrl+C to stop the server. If the browser opens before the server is ready, wait briefly and refresh.

To update, download and extract the newer release into a new folder, then use its launcher. Saved TV settings, pairing tokens, macros, menu definitions, and logs remain in the per-user configuration directory and are not stored in the release folder.

The package also retains the command-line interface. See [Use the command-line interface](#use-the-command-line-interface) for packaged and source commands.

## Build from source

Source builds require the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Git](https://git-scm.com/downloads). Confirm both are installed:

```text
dotnet --version
git --version
```

The .NET version must start with `10.`. Microsoft provides platform-specific setup instructions for [macOS](https://learn.microsoft.com/en-us/dotnet/core/install/macos), [Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows), and [Linux](https://learn.microsoft.com/en-us/dotnet/core/install/linux). On macOS, choose Arm64 for Apple silicon or x64 for Intel. Linux package names and repositories vary by distribution.

Clone, build, and test from macOS Terminal, Windows PowerShell, or a Linux shell:

```bash
git clone https://github.com/whipstein/SamsungController.git
cd SamsungController
dotnet restore SamsungController.sln
dotnet build SamsungController.sln --configuration Release --no-restore
dotnet test SamsungController.sln --configuration Release --no-build
```

A successful test run does not require a TV. The automated tests use fake transports and local fixtures.

To update an existing checkout later:

```bash
git pull
dotnet restore SamsungController.sln
dotnet build SamsungController.sln --configuration Release --no-restore
dotnet test SamsungController.sln --configuration Release --no-build
```

If a pull reports local changes, preserve or commit your configuration work before resolving it. Normal user configuration is stored outside the repository, but manually edited files inside `samples/` belong to the checkout.

### Start the web interface from source

From the repository root, run:

```bash
dotnet run --project src/SamsungController.Web
```

Leave that terminal open and browse to [http://127.0.0.1:5050](http://127.0.0.1:5050). Stop the server with Ctrl+C.

The server listens only on the local computer by default. That is intentional: the interface can send arbitrary TV keys and can unlock a raw JSON sender. It is not configured for access from another computer, tablet, or phone.

### Connect for the first time

1. Open **Connection**.
2. The default **Use saved combination** view lists saved display/menu/layout combinations. Select one, then select **Connect to display**. Use **Refresh list** to discover newly added files.
3. If your display is not listed, choose **Create new combination**. Enter its name and IP address in **1 · Display**.
4. In **2 · Menu**, choose **Use an existing menu**, select a menu definition, and choose its layout. The list scans user data, repository, and installation folders for YAML and JSON structures, labeling each file's location. Alternatively, choose **Define a new menu** and enter the TV model and firmware; firmware is the only context field.
5. Keep the recommended settings in **3 · Connection settings**. **Advanced connection settings** is collapsed by default and contains security, port, health-check, and warm-up options.
6. Select **Save combination**, then **Connect to display**. For a new menu, **Save & define menu** saves the combination and opens Build & Verify to enter its tree. Return to Connection when ready to connect.
7. Watch the TV and choose **Allow** when its authorization prompt appears. For future sessions, use the saved combination directly.

Use **Edit combination** while disconnected to change a saved display's address or settings, or link another menu/layout to that display. Choosing a menu in setup only updates the draft; **Save changes** applies it and **Cancel** discards it. The menu file stays at its original location, and existing verification is retained. File errors and **Reload menu file** are under **Files and connection details**.

The TV may take several seconds to show the prompt. When pairing succeeds, SamsungController stores the token for that TV address and reuses it on later connections. The saved address also enables the persistent **Connect**/**Disconnect** button at the top of every page.

To tune first-command reliability, use **Edit combination → Connection settings → Advanced connection settings**, then save. These settings are remembered with the display. The defaults send a WebSocket health check every 20 seconds, require a response within 10 seconds, wait 1.5 seconds after Samsung authorizes a new channel, and refresh the authenticated channel before the first command after five idle minutes. During that refresh the UI shows **Warming**, holds one requested command, and sends it exactly once. Set **Refresh channel after idle** to `0` to disable the proactive refresh. Set **Health-check timeout** to `0` only if a TV does not answer standard WebSocket PINGs. Increase the warm-up value if a newly powered-on TV still ignores or delays the first key.

If the active menu definition has a verified anchor, a successful web connection runs its preferred known-state sequence automatically. A verified `normal-video` anchor is preferred. This establishes the application's expected menu position before other navigation.

Use non-secure mode only if the TV does not expose port 8002: disable **Secure WebSocket** and leave the port empty to use port 8001. A custom port is rarely necessary.

### Header controls

The top bar is available on every page:

- **Connect/Disconnect** controls the saved TV without returning to the Connection page.
- **Quick access** runs pinned keys or behaviorally verified macros. A macro marked **Confirm before execution** asks “Are you sure?” before it sends anything. **Return to video** is included by default.
- **+ Add** adds a Samsung key or a verified macro from the current catalog. Remote keys and macros can also be pinned from their own pages.
- **Current menu** shows the leaf name of the menu position SamsungController expects to be active.
- **Menu profile** remains visible across every page. Green means every current check is fingerprinted for the displayed model/firmware combination; red shows how many checks remain and opens the complete verification workspace.

The current-menu value is a prediction, not feedback from the TV. Samsung's remote WebSocket does not report the on-screen cursor. Its confidence falls when an unknown key, interrupted route, lost connection, or failed visual result makes the position uncertain.

Samsung also does not acknowledge execution of individual remote keys. SamsungController can verify that the WebSocket is responsive, but it cannot distinguish a key the TV executed from one its menu service ignored. For that reason, an ambiguous send failure reconnects the channel without automatically retrying the key; retry manually only after checking the screen.

### Remote

The **Remote** page supplies common power, home, menu, direction, select, return, volume, and mute controls. Successful commands sent while menu recording is active are also captured by the authoring workflow.

Use **Custom command** for a key not shown on the remote:

1. Enter a Samsung key such as `KEY_SOURCE`.
2. Choose `Click`, `Press`, or `Release`.
3. Optionally enter a quick-access label.
4. Select **Send key** or **Add to quick access**.

Key names are intentionally not restricted. Review unfamiliar keys before sending them, and do not experiment with undocumented service-menu commands on a calibrated TV.

### Macros

The **Macros** page is a complete browser-based editor and visual verifier:

1. Enter a YAML catalog path and select **Load & validate**. You can also select **New macro** when the default or chosen file does not exist; the first save creates it.
2. The catalog loads with no macro selected. Select a macro to inspect it; select the same card again to clear the selection and empty the editor. Use the **×** beside a macro for an inline delete confirmation. Create, duplicate, or edit a macro, then choose its verified **Starting TV state** and use **Prepare selected start** to move the TV there without adding preparation keys to the draft.
3. Use the compact editor remote or the verified-menu list beside it. A successfully sent remote button becomes a key step; a successfully selected menu item executes immediately and becomes one high-level menu-call step. Select **Pause capture** to continue controlling or repositioning the TV without adding those actions, then resume capture. Set **Wait after each captured key** to preserve reliable key replay timing.
4. Build the remaining ordered operations from Samsung keys, explicit waits, calls to other saved macros, and calls to verified menu destinations. A menu call uses the same current-state-aware shortest-route planner as the Menu controls.
5. Move operations with the up/down controls, remove incorrect operations, then save. For a macro that can make substantial changes, enable **Confirm before execution**. It is off by default; when enabled, Replay and quick access show an **Are you sure?** dialog before any commands are sent. Catalog structure, nested calls, menu targets, cycles, durations, repeats, and expanded size are validated before the file is replaced.
6. Connect to the TV and select **Replay test**. Its first visible progress operation prepares the declared starting state through verified routes. If state is unknown, the planner uses a verified anchor; if the TV is already at the start, it sends nothing. Ordinary key and wait steps remain exact. A saved menu call intentionally delegates to the verified planner.
7. Inspect the TV. Select **Count pass** when the result is correct, or **Failed** to reset that macro to 0/3. Counts are stored independently in YAML, so you can alternate between macros without losing progress.
8. Three accepted replays mark the macro verified. Only verified macros can be pinned to the always-visible quick-access bar.
9. Use **Download YAML** to export the validated catalog. Loading another path provides the import workflow, and the remembered path is shared with the CLI and interactive console.

Changing only a macro's name, description, or confirmation preference preserves its passes; renaming also updates nested calls and a pinned quick-access entry. Changing its starting state or any operation resets that macro's behavioral verification and removes it from quick access. A called macro prepares its own declared start each time it is invoked. When the visual editor saves a hand-authored variable-based catalog, it writes normalized concrete step values while retaining the root variables.

The entire catalog is parsed and validated before the first TV key is sent. See [Macro format](docs/macros.md) for the YAML schema, variables, nested calls, validation limits, and safety behavior.

### Menu

The **Menu** page collects adjustable sliders, switches, selections, submenu selections, and indexed selection grids from every verified menu area. It remains locked until the complete menu structure is verified for the selected model, firmware, and every included configuration. The page opens with two unmistakable workspaces: **Adjust TV** can send commands, while **Enter current settings** maintains an independent command-free draft for recording values already visible on the display. The separate verified-destination navigator has been removed; use the Menu controls, Remote, macros, or quick-access commands instead.

For defaults that differ by bit depth, Picture Mode, or another setting, edit the control in **Build & Verify → Define menu tree → Condition-dependent defaults**. Add conditions and a value, put specific combinations first, then Save/Update. The matching default is used for estimates and explicit resets, not as a TV reading. See the [conditional-defaults tutorial](docs/menu-definition-file-format.md#defaults-that-depend-on-hdmi-input-or-signal).

Under **Saved-value conditions**, choose which conditions keep separate user values. For example, declare `valueContext: [external:hdmi-bit-depth, setting:picture-mode]` on Picture to share RGB/YCbCr values while retaining separate bit-depth/Picture Mode values. Children inherit; `valueContext: []` makes a setting shared. Use your menu's actual node IDs. Availability rules remain independent. **Menu → Enter current settings → Actual TV context** selects the context already on the TV without sending commands. Save each context, then download/load one [version-3 calibration file](docs/calibration-target-files.md#version-3-per-setting-contexts) for all of them. Apply a context-changing Picture Mode separately before adjusting its dependent controls. [Full JSON/YAML and UI instructions](docs/menu-definition-file-format.md#which-conditions-keep-separate-saved-settings).

Each card starts from its declared menu-definition default or a named saved current-TV state and tracks changes sent during the current application session. Sliders send Left/Right steps, switches send Select, and selections open the choice list, move from the predicted current option, and select the new option. A **submenu selection** performs the same value choice and then sends Return because the TV leaves that value list open. Choose **Update TV immediately** to send one change at a time, or choose **Wait for Apply** to stage several values. Staged dependencies are sent first: for example, 20 Point is enabled before its Interval/RGB controls, and Custom Color Space is selected before its Color/RGB controls. Because the complete display profile is already verified before this page unlocks, controls—including the 20 Point switch—do not show or request redundant per-change validation.

The page reevaluates every `disabledWhen` and `hiddenWhen` rule as its controlling value changes. A row marked `disabled: true` and its descendants remain visible as unavailable topology and cannot be executed, but the visible row still counts in Up/Down traversal. A hidden row and its descendants disappear from Menu and calculated routes remove them from sibling offsets. Menu definitions are model- and firmware-specific. YAML is the default storage format; JSON is also supported with the same schema and validation. See [Menu definition file format](docs/menu-definition-file-format.md) and [Menu definitions](docs/menu-model.md).

Expert calibration controls stay near the top of the page under dedicated **Expert settings**, **2pt white balance**, **20pt white balance**, and **Color** tabs. Expert settings is first. The 2pt tab stacks red, green, and blue Gain controls in one column and the three Offset controls in another. The 20pt tab pins its enable switch above the percentage controls, while the Color tab pins the Color Space dropdown above the custom-color controls. Every percentage/color RGB value uses the full slider with minus/plus buttons and a raw numeric input. The compact **Update behavior** panel is collapsed by default, and the permanent Picture, Sound, and other top-level menu tabs remain below the calibration section rather than being hidden with those options. A yellow **saved target differs** badge means the stored target profile does not match the current predicted value; opening the panel names each difference and its predicted and target values.

An **indexed selection grid** turns every selector option into a fixed row and the consecutive sliders following it into columns. This is used for all 20 Point White Balance percentages and all Custom Color choices, allowing every RGB target to be edited before one Apply operation. Existing definitions with an `Interval` percentage selector or a `Color` selector containing Red/Green/Blue are recognized automatically; new definitions can declare `controlType: indexed-selection` explicitly. The selector itself is not exposed as an editable dropdown because SamsungController chooses the required row while applying each cell.

Use **Enter current settings** when the TV already has settings you want SamsungController to treat as its starting point. Select the correct External HDMI Signal conditions, enter the values currently visible on the TV, then name the state and select **Save entered values**. This workspace never calls the TV: its draft is separate from staged adjustments. Named states—including indexed grid cells—stay in local application settings for the selected display/menu and input conditions. **Adjust TV** separately remembers desired target values for each combination. **Reset to defaults** runs the chosen verified reset, keeps the TV at factory defaults, and replaces both the current prediction and saved target profile for the active combination with its menu-definition defaults. **Reset & apply all** instead reapplies the saved calibration after resetting. Both are explicitly confirmed destructive operations; reset commands are never required by Display Verification.

A single portable `.samsung-calibration.json` file now holds **all saved input-condition combinations**. Use **Download all current settings** or **Download all targets** to export the whole collection. Load once under **Enter current settings** to install every included combination as a local current baseline, or under **Adjust TV** to stage every combination as targets without replacing current values. Changing the External HDMI Signal selectors automatically restores the matching set, including after restart. Neither loading nor switching sends commands, even in immediate mode; **Apply** affects only the active combination. Missing current values are labeled as assumed defaults, never copied from another signal mode. Imports validate all combinations before saving anything. Only load a file as current when the TV actually contains those values. See [Calibration files](docs/calibration-target-files.md) for step-by-step instructions and the combined JSON schema.

During **Apply**, **Reset to defaults**, or **Reset & apply all**, a red sticky progress strip identifies the current phase and key and provides **Cancel active update**. Cancel reaches the shared command token and prevents the next key after any already in-flight send or delay. It deliberately sends no cleanup, anchor, or return-to-video sequence after cancellation. Values known to have completed remain recorded; the interrupted menu position and partially adjusted indexed rows are treated as uncertain, so inspect the screen and record the TV's current settings before resuming.

Choose **Stay on last adjusted item** to leave the final control visible for inspection, or **Exit to normal video** to run the verified return anchor afterward. The Samsung remote channel does not report setting values, so load or create a matching saved current-TV state after changes made with the physical remote or another application. If behavior later disagrees with the verified profile, use **Remove validation** on the affected check under Display Verification rather than approving individual controls on this page.

Applying a batch reuses the current menu position between settings. Calculated cross-section routes return directly through submenu levels and move across the shared parent, without scrolling back through rows in menus being exited. The planner compares that route with the available verified alternatives; the exit preference applies after the entire batch.

### Build & Verify

Use **Build & Verify** when the supplied menu definition does not match the TV, or when adding a model and firmware combination. It does not require hand-editing YAML or JSON.

The page presents three setup stages and highlights the next required action:

1. **Define menu:** create the TV profile, choose YAML (the default) or JSON, select the active layout, and enter the complete menu in its on-screen order. The generated filename uses model and firmware only; for example, `qn90d-1296.yaml` or `.json`. The outline editor shows 20 lines, synchronized line numbers, and tree-aware indentation guides. Use two spaces per level, preview changes, and save. Mark rows as submenus, bounded sliders, ordered selections, submenu selections that require Return after choosing, indexed selection grids, switches, confirmation dialogs, or non-activated TV functions. Use `action` for a row such as Smart Calibration when selecting it starts a TV function: navigation stops with the row highlighted and never presses OK automatically. Use `disabled=true` for a row that is always gray, `disabledWhen` for a row that is conditionally gray, and `hiddenWhen` for a row that disappears based on a modeled setting. Keep named configurations for reordered layouts or changes that cannot be expressed through modeled settings.
2. **Define anchor:** choose the base menu, normally **Settings**. Define the return rule used exactly at that root (`KEY_RETURN` on the tested TV) and the rule used from any deeper state (`KEY_MENU, KEY_RETURN`). Save those rules, place the TV on normal video, and record only the keys that open the base menu. Successful buttons are sent to the TV and captured. This one entry recording seeds all topology-derived routes.
3. **Verify coverage:** the page displays the complete calculated verification plan: the two anchor-return checks, any state-specific return exception, and one representative line item for every affected topology branch. Each return line has an explicit **Prepare start** action that sends its displayed generated route; no hidden reset commands are added. Three accepted passes promote all routes covered by a line item. You do not record or verify each destination separately.

Adding, removing, reordering, or redefining menu items later regenerates the route set immediately. Structurally unchanged groups remain verified. Only a new or affected branch returns to the verification list, so an appended top-level branch usually adds one new line item while an edit inside an existing branch rechecks that branch only.

The authoring source is a reusable menu structure. Use **Export & use structure** to write a clean YAML or JSON copy into the repository's tracked `menu-definitions/` catalog and make that copy the active authoring source. Exported files contain topology, controls, conditions, routes, and timing, but no display-verification records or saved TV values. Files placed in that catalog are included in packaged releases.

Use the **Schema inspector** at the top of Build & Verify to troubleshoot a file before loading it. It accepts either one file or a directory, never sends TV commands, and reports each file's validity, format, identity, topology counts, calculated-route count, warnings, and exact errors. Personal verification sidecars are deliberately excluded. The active-file and repository-catalog shortcuts cover the two common checks. A valid result can be loaded directly. JSON errors include line and column where available.

New TV profiles start with an assumed-valid 800 ms screen-change wait and a faster 75 ms Left/Right value-adjustment wait, so you can record the first traversal immediately and apply larger slider changes efficiently. If playback needs adjustment, tune **System-wide timing** for Up/Down navigation, Left/Right adjustment, screen-change, and Return waits, then select a known traversal to test the profile. A draft's **Timing lab** can override the wait after an individual button.

Red menu items have no route in the active configuration; yellow items are covered by a recorded or topology-generated draft; normal-color items are verified. Open **Fine adjustments for individual menu items** for a small rename, insertion, deletion, parent change, TV-order correction, slider-bound change, or to edit ordered selection/submenu-selection/confirmation choices. A choice control's default is selected from its list. Arrow keys navigate this optional editor; the on-row buttons change custom order.

Samsung does not report these setting values to the controller. `disabled: true` permanently marks a row unavailable, `disabledWhen` does so when any declared condition matches, and `hiddenWhen` removes a row and its descendants. SamsungController updates predicted conditional state for changes it sends and recalculates affected offsets, but it cannot observe changes made with a physical remote or another application. After an outside change, restore the declared defaults or load a matching saved TV state on Menu before relying on navigation. Use a separate configuration for reordered layouts or conditions that are not represented by a controllable setting. A configuration's `conditions` text remains a human-readable checklist, not an automatically evaluated expression.

Collapsed authoring sections reopen when a setting inside them needs validation. The Menu page becomes available only after Display Verification is complete for the structure/display combination. Removing a verified setting also removes dependent descendants and routes; known-state anchor targets are protected.

Return-to-video behavior can be defined at three levels: a menu-root default, a deeper-menu default, and an exact state override. A return sequence recorded with a transition takes part in that transition's verification and is used to prepare subsequent passes. Use the default scripts unless a specific TV state consistently requires different keys.

### Display Verification

Open **Display Verification** after Build & Verify. This page is the final acceptance checklist for the loaded menu structure and its exact display combination, and completing it unlocks Menu. Start with **Carry forward existing verified work** to import valid timing, route, anchor, return-script, shared-slider, and selection evidence already recorded elsewhere in the app. Confirm the displayed model and firmware, then finish only the remaining representative behaviors.

Accepting the last required check automatically opens **Menu**, after any test adjustments are restored and the TV returns to its known state. Failed or incomplete checks stay on Display Verification. You can return to the completed checklist anytime to review or remove results.

For a visual control check, select **Run guided test**. The app chooses an accessible representative, automatically enables any declared prerequisite controls, navigates to it, and makes a safe visible adjustment: sliders move one step, selections advance to another declared option, switches toggle, and conditional checks set up the state to inspect. Confirm the TV and select **Count pass** or **Failed** on that same line. Before exiting the menu in either case, SamsungController restores the tested control and every temporary prerequisite to their prior predicted values in reverse order. Reset commands and destructive confirmations are excluded from required coverage. If automatic preparation is impossible, the reason appears directly on that verification line. The explicit-key remote remains available for manual correction.

Ordinary selections, submenu selections, indexed selections, switches, and confirmations each produce at most one shared behavior check. Conditional rows are represented by disabled, hidden, and combined behavior classes rather than one test per controller or row. Unverified timing, anchor, return-script, and menu-route lines all provide **Prepare start**, **Run test**, and their own **Count pass**/**Failed** confirmation in place; the third accepted pass promotes and fingerprints the result automatically. The page can also activate a check's required menu configuration without leaving the checklist. **Build & Verify** is linked only when the structure is missing recording data that must be authored first. Selecting **Remove validation** on a completed line removes only that check from the local display record; it does not alter the menu structure or other results.

Verification is saved in an independent personal-data `menu-verifications/<menu-id>.verification.json` sidecar, never beside or inside the distributable menu structure. The referenced YAML/JSON remains byte-for-byte untouched when tests are run, passed, failed, or removed. A sidecar can retain independent profiles for multiple display combinations. Each line has an ISO-8601 confirmation time and a SHA-256 fingerprint that includes the display combination and complete behavior group. Editing a dropdown's options therefore reopens its shared interaction-type check; changing one conditional rule reopens the corresponding conditional-behavior class; changing one route reopens that route; changing timing reopens timing. Unchanged results remain valid, and the persistent header turns green only when every current representative check matches.

### Protocol

The **Protocol** page is intended for diagnostics and research:

- Search or filter the latest 500 in-memory RX/TX messages.
- Copy a formatted payload or export the filtered view.
- Reveal pairing tokens separately from UUID, MAC, and IP address values; both groups are redacted by default in the browser.
- Send the known read/query requests for Eden or installed applications. Some firmware returns no response.
- Capture and label `/api/v2/` device snapshots, including while the WebSocket is disconnected, then compare or export them.
- Enable the raw JSON sender only for a payload you understand.

The browser's redaction does not sanitize the complete NDJSON session log. Treat exported views, device snapshots, session logs, and `tokens.json` as private.

## Use the command-line interface

The downloadable package includes the full CLI. Open a terminal in the extracted `SamsungController` folder and use one of these prefixes:

```text
./samsungctl                 # macOS or Linux
.\samsungctl.exe             # Windows PowerShell
```

When running from source, use:

```text
dotnet run --project src/SamsungController.Cli --
```

The examples below show the complete source command. Package users can replace `dotnet run --project src/SamsungController.Cli --` with the appropriate packaged executable above. Both forms use the same saved settings and tokens as the web interface.

### Pair and inspect status

```bash
dotnet run --project src/SamsungController.Cli -- connect 192.0.2.10
dotnet run --project src/SamsungController.Cli -- status
```

Approve the first connection on the TV. Secure port 8002 is the default. Samsung TVs commonly use a device certificate that is not publicly trusted; the default client relaxes validation only for this TV connection. Use `--strict-tls` only if the TV presents a certificate trusted by the computer.

For a TV that only supports the non-secure endpoint:

```bash
dotnet run --project src/SamsungController.Cli -- connect 192.0.2.10 --insecure
```

### Send keys

The last connected host is remembered:

```bash
dotnet run --project src/SamsungController.Cli -- key KEY_UP
dotnet run --project src/SamsungController.Cli -- key KEY_ENTER
dotnet run --project src/SamsungController.Cli -- key KEY_RIGHT --action Press
dotnet run --project src/SamsungController.Cli -- key KEY_RIGHT --action Release
```

A `Press` should normally be paired with a `Release`. Use `Click` for ordinary button presses.

### Validate and run macros

In a source checkout, first copy `samples/macros/macros.example.yaml` to
`user-data/macros.yaml`. The destination is ignored by Git and becomes your
working catalog. Listing and validation do not connect to the TV:

```bash
dotnet run --project src/SamsungController.Cli -- macro validate --macro-file user-data/macros.yaml
dotnet run --project src/SamsungController.Cli -- macro list --macro-file user-data/macros.yaml
```

After reviewing the sequence, run a named macro:

```bash
dotnet run --project src/SamsungController.Cli -- macro ExampleSequence --macro-file user-data/macros.yaml
```

The successfully used absolute macro path is remembered. Before a path has been selected, the default is `macros.yaml` in the per-user configuration directory. Ctrl+C cancels execution.

### Diagnose menu-definition schemas

Schema inspection does not connect to or control the TV. Pass one YAML/JSON file, or a directory to scan every supported file recursively:

```bash
dotnet run --project src/SamsungController.Cli -- menu validate menu-definitions
dotnet run --project src/SamsungController.Cli -- menu validate menu-definitions/odyssey-g9.json
```

With no path, `menu validate` checks `menu-definitions` under the current directory. Each result includes errors, non-blocking warnings, topology and route counts; personal verification sidecars are not included. The command returns exit code `0` when every file is valid and `2` if a definition is invalid or the directory contains no menu files, so it can also be used in scripts.

### Interactive console

The console keeps one authorized WebSocket open while accepting commands:

```bash
dotnet run --project src/SamsungController.Cli -- console
```

```text
samsungctl> state
samsungctl> key KEY_UP
samsungctl> key KEY_RIGHT Press
samsungctl> key KEY_RIGHT Release
samsungctl> macro list
samsungctl> macro TestNavigation
samsungctl> query apps
samsungctl> history
samsungctl> exit
```

The line editor supports Up/Down history, Left/Right editing, Home/End, Backspace/Delete, Escape to clear, Ctrl+A, Ctrl+E, and Ctrl+U. The latest 200 normal commands persist across sessions. `history clear` deletes them. Start with `--no-history` to keep commands only in memory for that session.

Raw JSON is disabled and excluded from history. To unlock it for the current console:

```bash
dotnet run --project src/SamsungController.Cli -- console --allow-raw
```

### Passive listener

```bash
dotnet run --project src/SamsungController.Cli -- listen
```

The listener prints inbound JSON until Ctrl+C. It is normal to see nothing after the connection event: the Samsung remote-control channel usually does not echo physical remote presses or publish the current menu cursor. Commands sent by another process use another WebSocket and appear in that process's log, not in this listener.

### CLI options

Run `dotnet run --project src/SamsungController.Cli -- help` for the complete usage text. Common options are:

| Option | Purpose |
| --- | --- |
| `--host <TV-IP>` | Override the remembered TV address. |
| `--name <name>` | Change the application name shown by the TV during pairing. |
| `--secure` / `--insecure` | Select secure port 8002 or non-secure port 8001. |
| `--port <number>` | Override the normal port. |
| `--strict-tls` | Require a publicly trusted TLS certificate. |
| `--pairing-timeout <seconds>` | Change the 90-second authorization timeout. |
| `--macro-file <path>` | Select and remember a macro catalog. |
| `--config-dir <path>` | Override the per-user settings and session directory. |
| `--token-file <path>` | Override the pairing-token file. |
| `--log <path>` | Override the NDJSON session-log path. |
| `--quiet` | Suppress diagnostic terminal output. |

### Publish a standalone CLI launcher

This creates a framework-dependent CLI folder; the .NET 10 runtime must still be installed:

```bash
dotnet publish src/SamsungController.Cli --configuration Release --output artifacts/samsungctl
```

Run it on macOS or Linux:

```bash
./artifacts/samsungctl/samsungctl status
```

Run it from Windows PowerShell:

```powershell
.\artifacts\samsungctl\samsungctl.exe status
```

## Configuration and private data

SamsungController creates its configuration directory on first use:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/SamsungController` |
| Windows | `%APPDATA%\SamsungController` |
| Linux | `$XDG_CONFIG_HOME/SamsungController`, or `~/.config/SamsungController` when `XDG_CONFIG_HOME` is unset |

Important contents include:

| Path | Contents |
| --- | --- |
| `settings.json` | Remembered host, connection options, macro/menu paths, quick access, named current-TV states, and UI preferences. |
| `tokens.json` | Host-specific Samsung authorization tokens. Keep private. |
| `macros.yaml` | Default user macro catalog, if you create it. |
| `display-definitions/` | Saved display/connection profiles and their menu-definition references. |
| `menu-definitions/` | Active and draft YAML or JSON menu definitions created by the UI. |
| `menu-verifications/` | Independent personal display-verification sidecars keyed by menu-structure ID; no menu topology is stored here. |
| `sessions/*.ndjson` | Complete timestamped protocol messages for connected sessions. Keep private. |
| `console-history.txt` | Up to 200 retained non-raw console commands. |

The Connection page lists both the read-only base structures shipped in the
installation and editable files under `menu-definitions/` in this user-data
directory. Linking a structure to a display writes only a reference into the
small display definition; it never copies the menu file. Use **Reload menu
file** after correcting the selected YAML or JSON outside the app. To edit an
installed structure, first use **Export & use structure** in Build & Verify to
explicitly create and activate an editable copy.

Display definitions are separate from menu structures. They may reference
user-data, repository, installation, or custom menu definitions and can group
multiple SDR/HDR/input/layout contexts for one physical display. Selecting a
display restores its default menu; the linked-menu selector switches among its
other saved contexts. See [Display definitions](docs/display-definitions.md).

Every YAML or JSON file found in those catalogs appears on Connection. A file
that cannot be parsed or validated is disabled in the selector, labeled
**INVALID**, and listed immediately below it with the exact diagnostic—normally
including a JSON line and column. Correct the file and select **Reload menu
file**; the app reparses it and regenerates its routes and verification checks.

Repository ignore rules cover the normal secret and session filenames, but they cannot protect copies or exports saved elsewhere. Keep TV-specific macros, saved states, sidecars, notes, and protocol exports under local application data or the ignored `user-data/` directory. Reviewed structure-only files belong in the tracked repository `menu-definitions/` catalog; they are included in releases for easy distribution. The files under `samples/` remain deliberately generic authoring tutorials.

## Troubleshooting

### The TV reports `ms.channel.timeOut`

The network connection reached the TV, but authorization was not approved before the pairing window closed.

1. On the TV, open **Settings > All Settings > Connection > External Device Manager > Device Connect Manager**. Firmware can place **Device Connect Manager** directly under **Connection**.
2. Enable **Access Notification**. Some models label the enabled choice **Always on**.
3. Open **Device List** and remove denied or stale SamsungController entries.
4. Connect again and select **Allow** on the TV.

Samsung also describes these controls in its [connection troubleshooting guidance](https://www.samsung.com/us/support/troubleshooting/TSG01109889/).

### A saved token is rejected

Forget the saved authorization and pair again:

```bash
dotnet run --project src/SamsungController.Cli -- forget
dotnet run --project src/SamsungController.Cli -- connect 192.0.2.10
```

Use `forget --host <TV-IP>` when tokens for multiple TVs are stored. You may also need to remove SamsungController from the TV's allowed-device list.

### The app cannot connect

- Confirm the TV and computer are on the same LAN and client isolation is disabled for that network.
- Confirm the TV's current IP address rather than relying on an old lease.
- Try secure port 8002 first. Use non-secure port 8001 only if the TV supports it.
- Wake the TV fully before connecting; network control availability during standby varies.
- Close another SamsungController process temporarily if the TV is refusing additional channels.
- Review the Connection error and the newest session log for the exact failure.

### The TV is discoverable but ports 8001 and 8002 time out

A VPN or network filter can allow Bonjour discovery while blocking the direct local TCP connection SamsungController needs. In this condition, deleting the pairing token will not help because the connection fails before authorization.

1. On the TV, enable **Settings > All Settings > Connection > Network > Expert Settings > IP Remote** and **Power On With Mobile**.
2. Open `http://<TV-IP>:8001/api/v2/` in a browser on the same computer. Device JSON confirms that the TV's local API is reachable; a timeout confirms that traffic is still being filtered or the TV service is unavailable.
3. Temporarily disconnect any VPN and retry. If that works, enable the VPN client's LAN exception before reconnecting the VPN.
4. In Proton VPN for macOS, open **Settings > Connection**, enable **Allow LAN connections**, and let Proton reconnect to apply the change. See [Proton VPN's LAN instructions](https://protonvpn.com/support/lan-connections).
5. If the failure remains, review local network filters such as LuLu, Little Snitch, or endpoint-security software for a rule blocking `SamsungController.Web`, the browser, or the TV address.

This exact pattern was verified on macOS with Proton VPN WireGuard: the TV advertised `_samsungmsf._tcp` normally, but `/api/v2/` and both control ports timed out until **Allow LAN connections** was enabled.

### The web address does not open

- Keep the `dotnet run` terminal open and wait for `Now listening on: http://127.0.0.1:5050`.
- Open `http://127.0.0.1:5050`, not the TV's address.
- If port 5050 is already occupied, stop the other process or temporarily override the URL. The following command works in macOS Terminal, Windows PowerShell, and a Linux shell:

```bash
dotnet run --project src/SamsungController.Web -- --urls http://127.0.0.1:5051
```

Then browse to `http://127.0.0.1:5051`. The sidebar still displays the default address because it is static UI text.

### A menu route reaches the wrong item

Do not approve a failed visual pass. Use the failed-traversal control or open **Build & Verify**, prepare a known source, and replay the draft. Determine whether the sequence itself is wrong or whether one button needs a longer wait. Adjust system-wide waits first when the issue affects many routes; use a per-button override for a single transition. Reverify the affected command three times.

## Safety and privacy

Samsung service menus and undocumented write requests can permanently alter TV configuration. Ordinary remote keys and previously reviewed macros are the intended control surface. Arbitrary key names and raw JSON exist for research and require the user to judge the payload.

Keep the web server on loopback, keep pairing tokens and logs private, and prefer read/query diagnostics before any experimental write. Do not publish protocol captures without reviewing them for tokens, IP addresses, UUIDs, MAC addresses, device names, and application data.

## Additional documentation

- [Complete beginner installation and usage guide](docs/getting-started.md)
- [Macro format and safety](docs/macros.md)
- [Menu definition file format tutorial](docs/menu-definition-file-format.md)
- [Menu definitions and predicted navigation](docs/menu-model.md)
- [Samsung protocol notes](docs/protocol.md)
- [Initial Samsung key list](docs/samsung-keys.md)
- [Development roadmap and checklist](docs/development-roadmap.md)
- [Architecture and implementation notes](docs/architecture.md)
- [Research log](docs/research-notes.md)

SamsungController is distributed under the [MIT License](LICENSE). The Samsung protocol behavior was independently implemented after consulting public traffic conventions and the Samsung portions of [ColorControl](https://github.com/Maassoft/ColorControl); no ColorControl source was copied.
