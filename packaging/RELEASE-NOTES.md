## Self-contained application packages

This release provides self-contained downloads for macOS, Windows, and Linux. No repository clone or separate .NET installation is required.

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
