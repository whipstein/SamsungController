# SamsungController beginner guide

This guide takes a first-time user from choosing a download through connecting,
verifying a TV menu, recording the TV's current values, making controlled
changes, creating macros, and installing a later SamsungController release.

SamsungController runs locally on your computer. The browser is the interface;
the launcher window is the server. Keep that window open while using the app.

## What you need

Before starting, have these items ready:

- A recent Samsung/Tizen TV and a Mac, Windows PC, or Linux computer on the
  same trusted local network.
- The TV's IPv4 address. A typical address looks like `192.168.1.125`.
- Access to the TV's physical remote for the first authorization and visual
  verification.
- A modern browser.
- About 5 minutes for installation and pairing. Creating and verifying a new
  menu structure takes longer and depends on the size of the TV menu.

SamsungController cannot see the highlighted menu row or read most setting
values from the TV. It predicts location from the commands it sends and asks
you to confirm visible results. Do not approve a test that did not visibly
pass.

## 1. Find the TV's IP address

On most Samsung TVs:

1. Open **Settings**.
2. Open **All Settings > Connection > Network > Network Status**.
3. Open **IP Settings** and note the IP address.

Menu wording varies by firmware. You can also find the TV in your router's
connected-device list. A DHCP reservation in the router is useful because it
keeps this address from changing.

## 2. Download the correct package

1. Open the [latest SamsungController release](https://github.com/whipstein/SamsungController/releases/latest).
2. Under **Assets**, choose the file matching both your operating system and
   processor.

| Computer | Package ending |
| --- | --- |
| Apple silicon Mac (M1, M2, M3, M4, or later) | `macos-arm64.tar.gz` |
| Intel Mac | `macos-x64.tar.gz` |
| Most Windows PCs with Intel or AMD processors | `windows-x64.zip` |
| Windows on Arm | `windows-arm64.zip` |
| Most Linux PCs with Intel or AMD processors | `linux-x64.tar.gz` |
| Arm64/aarch64 Linux | `linux-arm64.tar.gz` |

If you are unsure on macOS, open **Apple menu > About This Mac** and look for
**Chip** or **Processor**. On Windows, open **Settings > System > About** and
look at **System type**.

The downloads are self-contained. You do not need Git, the repository, the
.NET SDK, or a separate .NET runtime.

### Optional: verify the download

Every release includes `SHA256SUMS.txt`. This detects a damaged or replaced
archive.

On macOS or Linux, run this in the download directory:

```bash
shasum -a 256 SamsungController-*.tar.gz
```

On Windows PowerShell:

```powershell
Get-FileHash .\SamsungController-*.zip -Algorithm SHA256
```

Compare the displayed hash with the matching line in `SHA256SUMS.txt`.

## 3. Extract and start SamsungController

Do not run a file from inside a ZIP preview and do not move individual files
out of the extracted `SamsungController` folder.

### macOS

1. Double-click the downloaded `.tar.gz` file.
2. Open the extracted `SamsungController` folder.
3. Control-click **Start SamsungController.command** and choose **Open**.
4. Confirm **Open** and keep the Terminal window open.
5. If Gatekeeper still blocks it, open **System Settings > Privacy & Security**
   and choose **Open Anyway** for SamsungController.

The packages are not currently signed or notarized. Use this terminal fallback
only for an archive downloaded from the official release page:

```bash
xattr -dr com.apple.quarantine /path/to/SamsungController
```

Then open the launcher again.

### Windows

1. Right-click the downloaded ZIP and select **Extract All**.
2. Open the extracted `SamsungController` folder.
3. Double-click **Start SamsungController.cmd** and keep the command window
   open.
4. If SmartScreen appears, choose **More info > Run anyway** only after
   confirming the download came from the official release page.

### Linux

1. Extract the `.tar.gz` archive.
2. Open the extracted `SamsungController` folder.
3. Double-click **start-samsungcontroller.sh** and choose **Run**.
4. If your file manager does not offer Run, enable **Allow executing file as
   program** under file properties, or run `./start-samsungcontroller.sh` from
   a terminal.

### Confirm that it started

The launcher waits for the local server and opens
[http://127.0.0.1:5050](http://127.0.0.1:5050). If no browser appears, open that
address manually. Do not use the TV's IP address as the browser address.

Keep the launcher window open. Close it or press Ctrl+C there when you want to
stop SamsungController.

## 4. Understand the app layout

The left navigation follows the normal workflow:

1. **Connection** selects a TV, menu structure, and visible configuration.
2. **Remote** sends explicit remote-control buttons.
3. **Menu** displays verified settings and is where you enter values or update
   the TV.
4. **Macros** builds and verifies repeatable command sequences.
5. **Build & Verify** defines or repairs a TV's menu structure and navigation.
6. **Display Verification** confirms that a structure really matches a specific
   model, firmware, signal, picture mode, and input.
7. **Protocol** provides diagnostics and redacted protocol capture.

The header remains visible on every page:

- **Connect/Disconnect** controls the saved TV.
- **Quick access** runs pinned keys or verified macros.
- **Current menu** is SamsungController's predicted on-screen location. It is
  not a value reported by the TV.
- **Menu profile** is green only when the selected structure/display
  combination is completely verified. Select a red profile badge to open the
  work that remains.

Select **Page help** or a `?` bubble whenever one is available. It explains the
current page without sending anything to the TV.

## 5. Make the first connection

1. Fully power on the TV and leave it on normal video.
2. Open **Connection**.
3. Enter a friendly display name and the TV's IPv4 address.
4. Choose a **Menu structure** matching the TV model and firmware if one is
   available. The final label shows whether each copy comes from **User data**,
   the source **Repository**, or the application **Installation**; the selected
   copy also says **Active**. Copies with the same definition ID are listed
   separately so you can deliberately choose the installed base or a personal
   override.
5. Choose the **Menu configuration** that matches the rows currently visible on
   the TV. A configuration may represent a particular input, signal type,
   picture mode, or option-dependent layout.
6. Leave **Secure WebSocket** and **Accept TV certificate** enabled. Leave the
   port blank to use the normal secure port, 8002.
7. Select **Connect to display**.
8. Watch the TV and select **Allow** when its device-connection dialog appears.

The authorization dialog can take several seconds. SamsungController stores a
host-specific token after approval, so normal reconnects do not ask again.

After a successful connection, the header should say **Connected**. If the
selected structure has a verified normal-video anchor, SamsungController may
send that explicit sequence immediately to establish a known menu position.

### Check the basic remote

1. Open **Remote**.
2. Send one harmless directional key.
3. Open and close the normal TV settings menu.

If keys work, the network connection and authorization are ready.

## 6. Choose the next path

### A matching structure is already available

Keep it selected on **Connection**, then go to [Step 8: verify this display](#8-verify-the-structure-on-this-display).

### No structure matches the TV

Go to [Step 7: define a new menu structure](#7-define-a-new-menu-structure).

### You only need an ordinary remote

You can stop here and use **Remote**. Menu controls and menu-aware macros remain
locked until the full structure/display combination is verified.

## 7. Define a new menu structure

Open **Build & Verify**. Work from top to bottom; the highlighted stage is the
next required action.

### 7.1 Describe the menu

1. Enter the TV model and firmware exactly as shown in the TV's support or
   about screen.
2. Add signal type, picture mode, and input/source when they affect visible
   menu rows. These values are used in the suggested filename.
3. Choose YAML unless you specifically prefer JSON. Both formats represent the
   same nested topology.
4. Enter the menu rows in their exact on-screen order. Use two spaces per tree
   level. Pressing Enter preserves the previous line's starting indentation.
5. Select **Preview**. Fix any reported line and column error.
6. Review the generated tree and select **Save**. If the file already exists,
   confirm replacement when prompted.

For each row, define how it behaves:

- **submenu** opens a child list;
- **slider** changes within declared minimum and maximum values;
- **selection** chooses from an ordered option list;
- **submenu selection** opens an option list and requires Return after choosing;
- **switch** toggles on/off;
- **confirmation** opens a confirm/cancel dialog;
- **action** identifies a row that launches a TV function but should only be
  highlighted during navigation verification.

Use `disabled: true` for a row that is always grayed out. Use `disabledWhen` or
`hiddenWhen` for state-dependent rows. Disabled rows still occupy an Up/Down
position; hidden rows do not. A disabled parent automatically disables its
descendants.

The raw file tutorial documents every property and includes YAML and JSON
examples: [Menu definition file format](menu-definition-file-format.md).

### 7.2 Define the normal-video anchor

The anchor tells SamsungController how to enter the menu from normal video and
how to return to normal video from known depths.

1. Select the base menu, normally **Settings**.
2. Define the return sequence used at the base level. On some TVs this is
   `KEY_RETURN`.
3. Define the return sequence used from deeper menus. On some TVs this is
   `KEY_MENU`, then `KEY_RETURN`.
4. Save the return rules.
5. Manually place the TV on normal video.
6. Start the anchor recording and send only the keys needed to open the base
   menu. The in-app remote sends and records each key.
7. Stop and save the recording.

Every command sent during recording is visible in the recording list. Remove a
mistake with its `×`, or cancel the recording without entering another key.

### 7.3 Verify the minimum topology coverage

SamsungController derives most routes from the nested menu order. It asks for
representative checks, not one recording for every row.

For each displayed verification line:

1. Read both the **starting location** and expected result.
2. Select **Prepare start** if offered. This sends the displayed explicit route.
3. Select **Run test**.
4. Inspect the TV. For a submenu target, the submenu must be open and its child
   list visible. For a leaf target, the row must be highlighted without opening
   or changing it.
5. Select **Count pass** only if that exact result is visible. Select **Failed**
   otherwise.
6. Repeat until the required count is complete.

On the final accepted pass, SamsungController normally returns to video. If a
route fails, do not approve it. Manually correct the TV, return to a known state,
then determine whether the definition, row order, condition, or timing is wrong.

### 7.4 Tune navigation timing only when needed

Under **System-wide timing**:

- **Navigation** controls ordinary Up/Down spacing.
- **Value adjustment** controls Left/Right slider spacing and defaults to the
  faster value intended for repeated adjustments.
- **Screen change** covers Menu, Enter, Home, Exit, and Source.
- **Return** covers Return operations.

Use the timing test before marking a changed profile verified. Prefer a
system-wide change when several routes behave alike and a per-button wait only
for one exceptional transition.

### 7.5 Export a reusable structure

Select **Export & use structure** after the topology is clean. Exported YAML or
JSON contains no pairing token, saved TV values, protocol logs, or
display-specific verification. Those stay in local application data.

## 8. Verify the structure on this display

Open **Display Verification**. This binds reusable structure behavior to the
specific display combination shown at the top.

1. Confirm the model, firmware, signal, picture mode, and input/source.
2. Select **Carry forward existing verified work**. Unchanged, matching evidence
   is reused.
3. Work through the remaining groups in order.
4. For a route, use **Prepare start**, **Run test**, then **Count pass** or
   **Failed** on that same line.
5. For a slider, switch, or selection, select **Run guided test** and watch the
   exact control. SamsungController makes a small visible change.
6. Approve only the expected result. The test restores that control and any
   temporary prerequisite values before it exits.
7. Never validate a reset or destructive confirmation merely to satisfy
   coverage; they are excluded from required tests.

Equivalent controls share representative behavior checks. You should not need
to validate every slider or every dropdown. When all current fingerprints
match, every group and the persistent header profile badge turn green, and the
**Menu** page unlocks.

If you later modify the structure, only checks affected by that edit reopen.
Return to this page and complete the new lines.

## 9. Record the TV's current settings without changing it

This is the safest starting point before applying a calibration or a large set
of changes.

1. Open **Menu**.
2. Select **Enter current settings**. Do not use **Adjust TV** for this step.
3. Read each relevant value from the TV and enter it in the app. This workspace
   sends no TV commands.
4. Enter a clear state name such as `Filmmaker SDR before calibration`.
5. Select **Save entered values**.

Loading a saved current-TV state changes SamsungController's prediction
baseline; it does not update the television. If settings are later changed with
the physical remote or another application, record or load the matching state
again before relying on relative slider navigation.

## 10. Update TV settings safely

1. Open **Menu > Adjust TV**.
2. Load the current-TV state that actually matches the display.
3. Expand **Update behavior** and choose one mode:
   - **Update TV immediately** sends each changed control as you make it.
   - **Wait for Apply** stages changes so you can review them together.
4. Choose where the TV should finish:
   - **Stay on last adjusted item** is useful for visual inspection.
   - **Exit to normal video** runs the verified return path afterward.
5. Change sliders, switches, and selections. Conditional controls enable or
   disappear as their declared controllers change.
6. In staged mode, review the pending count and details, then select **Apply**.
7. Watch the sticky progress strip. If the TV and app become misaligned, select
   **Cancel active update** immediately.
8. After cancellation, inspect the TV before resuming. SamsungController does
   not send hidden cleanup keys, and already completed commands cannot be
   undone automatically.

For 2-point white balance, 20-point white balance, and custom color, use the
dedicated calibration tabs near the top. The 20-point enable switch lives in
the 20-point tab, and Color Space lives in the Color tab. Percentage and color
rows expose synchronized slider, minus/plus, and raw-number controls.

### Apply a known-good target file

Use **Portable target calibration** to load a `.samsung-calibration.json` file.
Loading validates and stages desired values but sends nothing. It is separate
from the current-TV baseline. Review the differences, then apply them.

Because Samsung does not report slider values, relative changes are trustworthy
only when the loaded current-TV state matches the display. **Reset & apply all**
is available as an explicitly confirmed workflow, but it is destructive and
should be used only when you intentionally want the declared defaults first.

## 11. Create and verify a macro

1. Open **Macros**.
2. Load a catalog path, or select **New macro** to create the default catalog.
3. Select **New macro** and enter a name. No existing macro is selected by
   default; selecting an already selected card clears the editor.
4. Choose the verified **Starting TV state**.
5. Select **Prepare selected start** to move the TV there without recording the
   preparation sequence as low-level steps.
6. Use the simplified remote. Successfully sent keys are added to the macro.
7. Select a verified menu item beside the remote to execute it and add one
   high-level menu call.
8. Use **Pause capture** when you need to reposition the TV without recording
   those keys.
9. Add explicit waits or calls to another saved macro when needed. Reorder or
   delete incorrect steps.
10. Enable **Confirm before execution** for a macro that can make substantial
    changes. It is off by default.
11. Save, then select **Replay test**.
12. Inspect the result and select **Count pass** or **Failed**. Three accepted
    replays mark the macro verified.
13. Add a verified macro to **Quick access** if you want it available on every
    page.

Changing operations or the starting state resets that macro's verification.
Changing only its description or confirmation preference does not.

## 12. Stop, restart, and update SamsungController

### Stop and restart

Close the launcher window or press Ctrl+C in it to stop the server. Run the same
launcher again to restart. Closing only the browser tab does not stop the
server.

### Install a newer release

1. Stop the old SamsungController process.
2. Download the new archive matching the same platform.
3. Extract it into a new folder. Do not overwrite files inside a running release
   folder.
4. Run the new launcher.
5. Confirm the saved TV, menu structure, verification status, macros, and states
   appear.
6. After the new version works, delete the old extracted application folder if
   desired.

User data is stored outside the application folder, so replacing a package does
not normally remove it:

| Platform | User-data location |
| --- | --- |
| macOS | `~/Library/Application Support/SamsungController` |
| Windows | `%APPDATA%\SamsungController` |
| Linux | `$XDG_CONFIG_HOME/SamsungController`, or `~/.config/SamsungController` |

Back up this directory before moving to another computer or making extensive
manual edits. `tokens.json` and complete session logs are private.

The app also scans the extracted application's `menu-definitions` directory.
You may delete a duplicate from the user-data `menu-definitions` directory after
selecting the **Installation** copy on Connection. Refresh the Connection page
after changing files outside the app. Editing an installed base through Build &
Verify creates a new user-data override instead of modifying the installation.

## 13. Troubleshooting checklist

### The browser never opens

- Keep the launcher window open.
- Open `http://127.0.0.1:5050` manually.
- If the launcher says port 5050 is in use, close the older SamsungController
  process before trying again.

### Connection fails with no authorization prompt

1. Confirm the TV's current IP address.
2. Confirm the computer and TV are on the same LAN or VLAN and guest/client
   isolation is disabled.
3. Open `http://<TV-IP>:8001/api/v2/` in a browser on the same computer.
4. If it times out, temporarily disconnect VPN software and retry. When that
   fixes it, enable the VPN's LAN-access option. Proton VPN on macOS calls this
   **Settings > Connection > Allow LAN connections**.
5. Check local filters such as LuLu, Little Snitch, or endpoint-security tools.
6. On the TV, enable **IP Remote** and **Power On With Mobile** if available.

Do not delete the pairing token when both ports time out; the connection is
failing before token authorization.

### The TV reports `ms.channel.timeOut`

1. Open the TV's **Device Connect Manager**.
2. Enable access notifications.
3. Remove a denied or stale SamsungController entry from **Device List**.
4. Connect again and approve the on-TV dialog before it closes.

### A first command after idle is ignored

Expand **Channel readiness** on Connection. SamsungController can refresh an
idle channel and hold one requested command until warm-up completes. Increase
the post-authorization warm-up if the TV still ignores the first key after
power-on.

### A route selects the wrong row

- Do not count the test as passed.
- Check whether a disabled row is still visible or a condition made a row
  disappear.
- Confirm the selected configuration matches the current input, signal, and
  picture mode.
- Use the failed-route tools to return to normal video and retest from the
  displayed starting location.
- Adjust system timing only if the key sequence is correct but the TV misses a
  command.

### The current menu becomes Unknown

SamsungController intentionally drops confidence after a lost connection,
unknown key, cancelled batch, or navigation mismatch. Return to normal video
with the physical remote or a verified quick action, then run the known anchor
before continuing.

## Safety and privacy

- Keep the server bound to `127.0.0.1`; the interface can send arbitrary TV
  keys and protocol payloads.
- Avoid undocumented service-menu commands on a calibrated display.
- Do not share `tokens.json`, complete NDJSON session logs, or unreviewed
  protocol captures.
- Browser exports redact tokens and device identifiers by default, but the
  complete session log is not sanitized.
- Use **Cancel active update** as soon as a large adjustment is visibly wrong.

For advanced schemas and diagnostics, continue with the [main README](../README.md)
and the documentation index at its end.
