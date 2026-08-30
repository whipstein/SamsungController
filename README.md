# SamsungController

SamsungController is a local, cross-platform controller for modern Samsung/Tizen TVs. It can pair with a TV over the LAN, send remote-control keys, run repeatable YAML macros, navigate a visually verified menu map, and capture Samsung WebSocket traffic for troubleshooting or protocol research.

The web interface is the recommended way to use the application. A command-line interface (`samsungctl`) is also included for scripting, terminal use, and diagnostics.

## Features

- Secure (`wss://`, normally port 8002) and non-secure (`ws://`, normally port 8001) Samsung connections
- First-use TV authorization with host-specific token storage
- Remote keys using `Click`, `Press`, and `Release` actions
- Browser-edited YAML macros with variables, nested calls, explicit waits, progress, and three-pass visual verification
- Verified, state-aware menu navigation and a guided menu-map builder
- Persistent quick-access buttons and an expected-current-menu indicator
- Searchable RX/TX protocol messages, redacted browser views, and NDJSON session logs
- Interactive terminal console with persistent command history
- Self-contained macOS, Windows, and Linux downloads for x64 and Arm64 computers

Pairing and basic remote keys may work with many recent Samsung/Tizen TVs, but menu layouts, key behavior, available queries, and authorization details vary by model and firmware. No bundled menu route is presented as verified for a real TV.

## Before you install

Both installation methods require:

1. A computer and Samsung TV on the same trusted local network.
2. The TV's IPv4 address. SamsungController does not currently discover TVs automatically. A DHCP reservation is recommended so the address does not change.
3. A current web browser for the local web interface.

The downloadable release is recommended for most users. It is self-contained and does not require Git, a repository clone, the .NET SDK, or a separate .NET runtime. Building from source remains available for contributors and users who want to modify the code.

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
2. Enter a display name and the TV's IP address.
3. Leave **Secure WebSocket** enabled, **Accept TV certificate** enabled, and the port empty for the normal secure port 8002.
4. Select **Connect to display**.
5. Watch the TV and choose **Allow** when its authorization prompt appears.

The TV may take several seconds to show the prompt. When pairing succeeds, SamsungController stores the token for that TV address and reuses it on later connections. The saved address also enables the persistent **Connect**/**Disconnect** button at the top of every page.

Expand **Channel readiness** to tune first-command reliability. The defaults send a WebSocket health check every 20 seconds, require a response within 10 seconds, wait 1.5 seconds after Samsung authorizes a new channel, and refresh the authenticated channel before the first command after five idle minutes. During that refresh the UI shows **Warming**, holds one requested command, and sends it exactly once. Set **Refresh channel after idle** to `0` to disable the proactive refresh. Set **Health-check timeout** to `0` only if a TV does not answer standard WebSocket PINGs. Increase the warm-up value if a newly powered-on TV still ignores or delays the first key.

If the active menu definition has a verified anchor, a successful web connection runs its preferred known-state sequence automatically. A verified `normal-video` anchor is preferred. This establishes the application's expected menu position before other navigation.

Use non-secure mode only if the TV does not expose port 8002: disable **Secure WebSocket** and leave the port empty to use port 8001. A custom port is rarely necessary.

### Header controls

The top bar is available on every page:

- **Connect/Disconnect** controls the saved TV without returning to the Connection page.
- **Quick access** runs pinned keys or behaviorally verified macros. A macro marked **Confirm before execution** asks “Are you sure?” before it sends anything. **Return to video** is included by default.
- **+ Add** adds a Samsung key or a verified macro from the current catalog. Remote keys and macros can also be pinned from their own pages.
- **Current menu** shows the leaf name of the menu position SamsungController expects to be active.

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
4. Build the remaining ordered operations from Samsung keys, explicit waits, calls to other saved macros, and calls to verified menu destinations. A menu call uses the same current-state-aware shortest-route planner as clicking that destination on the Menu page.
5. Move operations with the up/down controls, remove incorrect operations, then save. For a macro that can make substantial changes, enable **Confirm before execution**. It is off by default; when enabled, Replay and quick access show an **Are you sure?** dialog before any commands are sent. Catalog structure, nested calls, menu targets, cycles, durations, repeats, and expanded size are validated before the file is replaced.
6. Connect to the TV and select **Replay test**. Its first visible progress operation prepares the declared starting state through verified routes. If state is unknown, the planner uses a verified anchor; if the TV is already at the start, it sends nothing. Ordinary key and wait steps remain exact. A saved menu call intentionally delegates to the verified planner.
7. Inspect the TV. Select **Count pass** when the result is correct, or **Failed** to reset that macro to 0/3. Counts are stored independently in YAML, so you can alternate between macros without losing progress.
8. Three accepted replays mark the macro verified. Only verified macros can be pinned to the always-visible quick-access bar.
9. Use **Download YAML** to export the validated catalog. Loading another path provides the import workflow, and the remembered path is shared with the CLI and interactive console.

Changing only a macro's name, description, or confirmation preference preserves its passes; renaming also updates nested calls and a pinned quick-access entry. Changing its starting state or any operation resets that macro's behavioral verification and removes it from quick access. A called macro prepares its own declared start each time it is invoked. When the visual editor saves a hand-authored variable-based catalog, it writes normalized concrete step values while retaining the root variables.

The entire catalog is parsed and validated before the first TV key is sent. See [Macro format](docs/macros.md) for the YAML schema, variables, nested calls, validation limits, and safety behavior.

### Menu

The **Menu** page contains verified destinations only. Click a destination to calculate and immediately run a proven route from the expected current position. The route panel remains available afterward for command inspection.

If the expected position is unknown, run a verified anchor under **Resynchronize**. If a traversal reaches the wrong place, use its failure control. SamsungController captures the attempted keys for diagnosis, marks the prediction uncertain, and tries to return the TV to normal video.

Menu definitions are model-, firmware-, input-, signal-, and picture-mode-sensitive. Do not assume a route verified in one SDR context is valid in HDR, on another input, or on different firmware. See [Menu definitions](docs/menu-model.md) for the data model and confidence rules.

### Build & Verify

Use **Build & Verify** when the supplied menu definition does not match the TV, or when adding a model and firmware combination. It does not require hand-editing YAML.

The page presents five setup steps and highlights the next recommended action. Successful actions open and scroll to the next step automatically:

1. **TV profile:** enter the model number and firmware version first. The default name is generated as `<model> · firmware <version>`. The YAML filename starts with those values and appends signal type, picture mode, and input/source when they are specific rather than `any`; for example, `qn90d-1296-sdr-filmmaker-mode-hdmi-1.yaml`. You may override either generated value. Create the profile to save it under the per-user `menu-definitions` directory and load it automatically. If that file already exists, the UI requires an explicit replacement confirmation and warns that its existing tree, routes, and verification state will be overwritten.
2. **Configuration:** describe the settings that produce the menu layout currently visible on the TV. Add a named configuration whenever selections make rows appear, disappear, or move, such as `Game Mode off` and `Game Mode on`. Choose the active configuration before recording or navigating; it is also shown beside **Current menu** in the persistent header. Switching it resets the expected position to unknown.
3. **Menu outline:** the editor is automatically populated from the selected branch in the active YAML and shows 20 visible lines with a synchronized line-number gutter. Tree-aware vertical guides appear only for populated indentation branches and end when a later item returns to a shallower level. Edit the hierarchy directly using two spaces per level, choose **Preview changes**, and then choose **Save & keep editing** or **Save & continue** to advance to recording; any edit requires a new preview. Formatting errors appear beside the outline with the affected line number and a correction. **Reload branch from YAML** discards unsaved text and restores the saved version. Mark each row as a sub-menu, slider, selection, switch, or confirmation. A slider requires numeric bounds, such as `Brightness {slider; default=50; min=0; max=100}`. A selection requires its TV-ordered choices, such as `Picture Mode {selection; default=Filmmaker Mode; options=Standard|Movie|Filmmaker Mode}`. Use `Adaptive Picture {switch; default=off}` for a switch. An action dialog such as Reset Picture uses `Reset Picture {confirmation; default=Cancel; options=Reset|Cancel}`, where the default is the initially highlighted choice. A row that stays in place but becomes gray can declare the controlling value, such as `Brightness {slider; default=50; min=0; max=100; disabledWhen=adaptive-picture=on}`. Matching lines retain stable IDs and new IDs are generated. The outline normally represents the complete selected branch, so deleting a line previews and saves that item as a removal. Enable **This is a partial outline** only when omitted items should remain.
4. **Record route:** choose an anchor or a source and target transition, then use the on-page remote. Every accepted button is sent to the TV immediately and added to the draft. Remove a mistaken command with its × button, undo it, or cancel without entering a key. **Also record return to normal video** captures the forward and return sequences as one draft with one validation count.
5. **Verify:** after saving, the page moves to the new draft automatically. Prepare its source, replay it, and count a pass only when the TV reaches the expected menu position. Each draft keeps its own count. The third pass verifies the route, returns the TV to normal video, and moves to the next unrecorded target when one remains.

New TV profiles start with an assumed-valid 800 ms screen-change wait, so you can record the first traversal immediately. If playback needs adjustment, tune **System-wide timing** for default, screen-change, and return waits, then select a known traversal to test the profile. A draft's **Timing lab** can override the wait after an individual button.

Red menu items have no recorded command in the active configuration; yellow items have a draft that still needs verification; normal-color items are verified. Topology-only items may remain red. Open **Fine adjustments for individual menu items** for a small rename, insertion, deletion, parent change, TV-order correction, slider-bound change, or to edit ordered selection/confirmation choices. A choice control's default is selected from its list. Arrow keys navigate this optional editor; the on-row buttons change custom order.

Samsung does not report these setting values to the controller. Declared defaults and disabled/gray rules document the expected setup but do not track live TV changes. A disabled row remains in its menu-order position; use a separate configuration only when a setting actually adds, removes, or reorders rows. If a setting is changed with the physical remote or another application, manually select the matching menu configuration before using a verified traversal. A configuration's `conditions` text is a human-readable checklist, not an automatically evaluated expression.

Collapsed authoring sections reopen when a setting inside them needs validation. Verified items then appear on the ordinary Menu page. Removing a verified setting also removes dependent descendants and routes; known-state anchor targets are protected.

Return-to-video behavior can be defined at three levels: a menu-root default, a deeper-menu default, and an exact state override. A return sequence recorded with a transition takes part in that transition's verification and is used to prepare subsequent passes. Use the default scripts unless a specific TV state consistently requires different keys.

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
| `settings.json` | Remembered host, connection options, macro/menu paths, quick access, and UI preferences. |
| `tokens.json` | Host-specific Samsung authorization tokens. Keep private. |
| `macros.yaml` | Default user macro catalog, if you create it. |
| `menu-definitions/` | Active and draft menu definition YAML files created by the UI. |
| `sessions/*.ndjson` | Complete timestamped protocol messages for connected sessions. Keep private. |
| `console-history.txt` | Up to 200 retained non-raw console commands. |

Repository ignore rules cover the normal secret and session filenames, but they cannot protect copies or exports saved elsewhere. In a source checkout, keep TV-specific macros, menu definitions, notes, and exports under the ignored `user-data/` directory. The tracked files under `samples/` are deliberately generic, unverified templates and should never be used as the live working files for a real TV.

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

- [Macro format and safety](docs/macros.md)
- [Menu definitions and predicted navigation](docs/menu-model.md)
- [Samsung protocol notes](docs/protocol.md)
- [Initial Samsung key list](docs/samsung-keys.md)
- [Development roadmap and checklist](docs/development-roadmap.md)
- [Architecture and implementation notes](docs/architecture.md)
- [Research log](docs/research-notes.md)

SamsungController is distributed under the [MIT License](LICENSE). The Samsung protocol behavior was independently implemented after consulting public traffic conventions and the Samsung portions of [ColorControl](https://github.com/Maassoft/ColorControl); no ColorControl source was copied.
