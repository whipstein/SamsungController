# SamsungController downloadable package

This is a self-contained SamsungController release. It includes the application and its .NET runtime; a repository clone, .NET SDK, and separate installer are not required.

If this is your first time using SamsungController, start with the
[complete beginner guide](docs/getting-started.md). It continues from download
and first launch through pairing, menu setup, display verification, entering
current values, safely applying changes, macros, upgrades, and troubleshooting.

## Start the web interface

Keep the complete extracted folder together. The launcher starts the local server, waits for it to become ready, and opens `http://127.0.0.1:5050` in the default browser.

### macOS

1. Double-click the downloaded `.tar.gz` file to extract it.
2. Open the extracted `SamsungController` folder.
3. Control-click **Start SamsungController.command**, choose **Open**, then confirm **Open**. Keep its Terminal window open while using the application.
4. If macOS blocks the unsigned application, open **System Settings > Privacy & Security** and choose **Open Anyway** for SamsungController. As a terminal fallback, run `xattr -dr com.apple.quarantine` followed by a space and the path to the extracted folder.

Choose `macos-arm64` for an Apple silicon Mac or `macos-x64` for an Intel Mac.

### Windows

1. Right-click the downloaded `.zip` file and choose **Extract All**. Do not run the launcher from inside the ZIP preview.
2. Open the extracted `SamsungController` folder.
3. Double-click **Start SamsungController.cmd**. Keep its command window open while using the application.
4. These first packages are unsigned. If Microsoft Defender SmartScreen appears, choose **More info > Run anyway** only if the file came from the official SamsungController GitHub release.

Choose `windows-x64` for a normal Intel/AMD Windows PC or `windows-arm64` for a Windows on Arm device.

### Linux

1. Extract the downloaded `.tar.gz` file.
2. Open the extracted `SamsungController` folder.
3. Double-click **start-samsungcontroller.sh** and choose **Run**. Keep the process running while using the application.
4. If the file manager does not offer Run, open the file's **Properties > Permissions** and enable execution. You can also launch `./start-samsungcontroller.sh` from a terminal.

Choose `linux-x64` for most Intel/AMD PCs or `linux-arm64` for an Arm64/aarch64 system. A graphical browser and either `xdg-open` or `gio` are required for automatic browser opening; otherwise open the local URL manually.

## First connection

1. Make sure the computer and Samsung TV are on the same trusted local network.
2. Enter the TV's IPv4 address on the Connection page.
3. Keep Secure WebSocket and Accept TV certificate enabled under normal conditions.
4. Select Connect and choose Allow on the TV when prompted.

The launcher and server bind only to this computer. Closing the launcher window or pressing Ctrl+C stops the server. TV settings, tokens, macros, menu definitions, and logs remain in the normal per-user configuration directory when the package is replaced with a newer version.

## If the TV connection times out

VPNs and network filters can allow the TV to appear on the network while blocking its control connection. Before removing the saved pairing token:

1. Open `http://<TV-IP>:8001/api/v2/` in a browser on the same computer. A timeout indicates a network path or TV-service problem, not a rejected token.
2. Temporarily disconnect the VPN and retry SamsungController.
3. If that works, enable the VPN's LAN-access exception. In Proton VPN for macOS, use **Settings > Connection > Allow LAN connections**, then reconnect the VPN.
4. Check filters such as LuLu, Little Snitch, or endpoint-security software for a rule blocking SamsungController or the TV address.

See the included `README.md` for the full connection troubleshooting sequence.

## Optional command line

The package also includes the complete `samsungctl` CLI.

On macOS or Linux, open a terminal in this folder:

```text
./samsungctl help
./samsungctl status
./samsungctl key KEY_HOME
```

On Windows, open PowerShell in this folder:

```text
.\samsungctl.exe help
.\samsungctl.exe status
.\samsungctl.exe key KEY_HOME
```

See the included `README.md` for pairing recovery, macros, console mode, menu authoring, protocol capture, privacy, and safety guidance.

## Security

The web interface can send arbitrary remote keys and raw protocol requests, so leave it bound to `127.0.0.1`. Pairing tokens and full protocol logs are private. Do not publish them without reviewing their contents.
