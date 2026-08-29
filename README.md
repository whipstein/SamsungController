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

The Samsung S95F is the primary real-TV test target. Basic pairing and remote keys may work with other recent Samsung/Tizen TVs, but menu layouts, key behavior, available queries, and authorization details vary by model and firmware.

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
- **Quick access** runs pinned keys or behaviorally verified macros. **Return to video** is included by default.
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
2. Create, duplicate, or edit a macro. Build its ordered operations from Samsung keys, explicit waits, and calls to other saved macros. Every key supports action, repeat count, and an optional wait after each send.
3. Move operations with the up/down controls, remove incorrect operations, then save. Catalog structure, nested calls, cycles, durations, repeats, and expanded size are validated before the file is replaced.
4. Connect to the TV and select **Replay test**. Replay sends only the explicit saved macro; it does not add a menu reset or starting-state preparation.
5. Inspect the TV. Select **Count pass** when the result is correct, or **Failed** to reset that macro to 0/3. Counts are stored independently in YAML, so you can alternate between macros without losing progress.
6. Three accepted replays mark the macro verified. Only verified macros can be pinned to the always-visible quick-access bar.
7. Use **Download YAML** to export the validated catalog. Loading another path provides the import workflow, and the remembered path is shared with the CLI and interactive console.

Changing only a macro's name or description preserves its passes; renaming also updates nested calls and a pinned quick-access entry. Any operation change resets that macro's behavioral verification and removes it from quick access. When the visual editor saves a hand-authored variable-based catalog, it writes normalized concrete step values while retaining the root variables.

The entire catalog is parsed and validated before the first TV key is sent. See [Macro format](docs/macros.md) for the YAML schema, variables, nested calls, validation limits, and safety behavior.

### Menu

The **Menu** page contains verified destinations only. Click a destination to calculate and immediately run a proven route from the expected current position. The route panel remains available afterward for command inspection.

If the expected position is unknown, run a verified anchor under **Resynchronize**. If a traversal reaches the wrong place, use its failure control. SamsungController captures the attempted keys for diagnosis, marks the prediction uncertain, and tries to return the TV to normal video.

Menu definitions are model-, firmware-, input-, signal-, and picture-mode-sensitive. Do not assume an S95F SDR route is valid in HDR or on a different firmware. See [Menu definitions](docs/menu-model.md) for the data model and confidence rules.

### Build & Verify

Use **Build & Verify** when the supplied menu definition does not match the TV, or when adding a new TV interface. This is an advanced visual workflow, but it does not require hand-editing YAML.

1. Select **New TV interface** and enter a definition ID, name, TV model, firmware, signal type, picture mode, and input/source. The new YAML file is saved in the per-user `menu-definitions` directory and loaded automatically.
2. Expand **Define the overall menu tree**. Add and arrange named positions before recording routes. Custom order can mirror the TV; alphabetical order is only a viewing aid. Arrow keys move through the tree, and the on-screen up/down controls reorder siblings in custom mode.
3. Read the node colors: red means no command has been recorded; yellow means a command exists but has not passed verification; the normal color means verified.
4. Under **Describe the recording**, choose an anchor or a source and target transition. For transitions, **Also record return to normal video** captures the forward and return sequences as one draft with one validation count.
5. Select **Prepare source** when an existing verified route can place the TV correctly, then **Start live recording**. Use the on-screen controller; each successful button is both sent to the TV and added to the sequence. Undo, clear, or cancel as needed, then save the draft.
6. Tune **System-wide timing** for default, screen-change, and return waits. Select a known traversal and use its test control before accepting the profile. A draft's **Timing lab** can override the wait after an individual button.
7. In **Timing lab and visual validation**, prepare the source, replay the draft, and confirm what you saw on the TV. Each draft remembers its own count. Three accepted visual passes make it verified; the third pass returns to normal video by default.

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
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100
dotnet run --project src/SamsungController.Cli -- status
```

Approve the first connection on the TV. Secure port 8002 is the default. Samsung TVs commonly use a device certificate that is not publicly trusted; the default client relaxes validation only for this TV connection. Use `--strict-tls` only if the TV presents a certificate trusted by the computer.

For a TV that only supports the non-secure endpoint:

```bash
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100 --insecure
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

Listing and validation do not connect to the TV:

```bash
dotnet run --project src/SamsungController.Cli -- macro validate --macro-file samples/macros/macros.example.yaml
dotnet run --project src/SamsungController.Cli -- macro list --macro-file samples/macros/macros.example.yaml
```

After reviewing the sequence, run a named macro:

```bash
dotnet run --project src/SamsungController.Cli -- macro TestNavigation --macro-file samples/macros/macros.example.yaml
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

Repository ignore rules cover the normal secret and session filenames, but they cannot protect copies or exports saved elsewhere.

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
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100
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
