# SamsungController

SamsungController is a cross-platform Samsung TV control and protocol research
utility. Version 0.1 focuses on the smallest useful vertical slice: pair with a
modern Samsung/Tizen TV, persist its token, send remote keys, reconnect, and
record every WebSocket message without throwing unknown events away.

The primary test target is a Samsung S95F. Real-TV behavior still needs to be
validated; see the [verification checklist](docs/architecture.md#s95f-verification-checklist).

## Current scope

- Plain `net10.0` core library for macOS, Windows, and Linux
- Secure port 8002 and non-secure port 8001
- Samsung pairing and host-specific token persistence
- `Click`, `Press`, and `Release` remote-key commands
- Arbitrary Samsung key strings for research
- Complete RX/TX NDJSON session recording
- Unknown/malformed message preservation
- Connection state and bounded automatic reconnect
- CLI commands: `connect`, `key`, `listen`, `status`, and `forget`
- Fake-transport tests that do not require a TV

Automatic discovery, macros, menu-state prediction, and the Blazor UI are
deliberately deferred until the S95F transport is proven.

## Requirements

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
No platform-specific desktop runtime is required.

## Build and test

```bash
git clone https://github.com/whipstein/SamsungController.git
cd SamsungController
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

The same commands work in Terminal on macOS/Linux and PowerShell on Windows.
CI runs them on all three operating systems.

## First pairing

Run from the repository:

```bash
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100
```

Approve the SamsungController prompt on the TV. Secure `wss://` port 8002 is the
default. Samsung TVs generally present a self-signed certificate, so validation
is relaxed only for this WebSocket. Use `--strict-tls` if your TV presents a
trusted certificate.

If the TV only exposes the non-secure endpoint:

```bash
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100 --insecure
```

Give the TV a DHCP reservation or static LAN address so the saved token remains
associated with the right host.

## Send remote keys

The last connected TV is remembered:

```bash
dotnet run --project src/SamsungController.Cli -- key KEY_UP
dotnet run --project src/SamsungController.Cli -- key KEY_ENTER
dotnet run --project src/SamsungController.Cli -- key KEY_RIGHT --action Press
dotnet run --project src/SamsungController.Cli -- key KEY_RIGHT --action Release
```

Keys are not allowlisted, so `KEY_WHATEVER` can be tried for protocol research.
That flexibility is not permission to send unknown service-menu commands.

To use `samsungctl` directly during development:

```bash
dotnet publish src/SamsungController.Cli -c Release -o artifacts/samsungctl
./artifacts/samsungctl/samsungctl status
```

On Windows, run `artifacts\samsungctl\samsungctl.exe`.

## Listen and capture traffic

```bash
dotnet run --project src/SamsungController.Cli -- listen
```

`listen` prints complete inbound and outbound JSON until Ctrl+C. All commands
also write full NDJSON sessions under the per-user configuration directory:

| Platform | Default directory |
| --- | --- |
| macOS | `~/Library/Application Support/SamsungController` |
| Windows | `%APPDATA%\SamsungController` |
| Linux | `$XDG_CONFIG_HOME/SamsungController` or `~/.config/SamsungController` |

Use `--log <path>`, `--token-file <path>`, or `--config-dir <path>` to override
those locations.

Pairing responses and session recordings can contain the Samsung token. Keep
them private. Repository ignore rules exclude `tokens.json`, `sessions/`, and
`*.ndjson`, but an explicit path outside those patterns is still your
responsibility.

## Recover from a rejected token

If the TV rejects a previously saved authorization:

```bash
dotnet run --project src/SamsungController.Cli -- forget
dotnet run --project src/SamsungController.Cli -- connect 192.168.1.100
```

You may also need to remove the controller from the TV's allowed-device list.

## Design and protocol notes

- [Architecture, token lifecycle, and assumptions](docs/architecture.md)
- [Protocol notes](docs/protocol.md)
- [Initial key list](docs/samsung-keys.md)
- [Research log template](docs/research-notes.md)

The protocol behavior was independently implemented after consulting Samsung
traffic conventions and the Samsung portions of
[ColorControl](https://github.com/Maassoft/ColorControl). ColorControl is GPLv3;
no source was copied into this MIT-licensed project.

## Safety

Samsung service menus and undocumented writes can permanently alter television
configuration. SamsungController 0.1 only sends normal remote-key requests. Any
future service, experimental, or direct-setting write must be separately
classified, explicitly enabled, and preceded by read/query diagnostics where
possible.
