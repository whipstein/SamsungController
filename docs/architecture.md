# SamsungController 0.1 architecture

This document records the design before the first transport implementation. The
repository is an independent implementation. ColorControl is used only as a
behavioral reference; no ColorControl source is copied.

## Boundaries and dependency direction

```text
samsungctl
    -> SamsungController.Automation
    -> IMacroCommandTarget adapter
    -> SamsungTvClient
    -> ClientWebSocketSamsungTransport
    -> Samsung TV

SamsungController.Web
    -> singleton SamsungControllerService
    -> SamsungController.Automation + SamsungTvClient
    -> ClientWebSocketSamsungTransport
    -> Samsung TV
```

- `SamsungController.Core` targets plain `net10.0` and contains no desktop,
  registry, COM, WMI, or operating-system service dependencies.
- `ISamsungTransport` owns WebSocket framing only. Tests replace it with an
  in-memory transport.
- `SamsungTvClient` owns handshake interpretation, token lifecycle, remote-key
  messages, state, retry/reconnect behavior, and message publication.
- `SamsungController.Automation` owns macro models, YAML parsing, validation,
  plan expansion, cancellation, and progress events, plus menu-definition
  parsing, graph planning, predicted state, confidence, anchors, and navigation
  execution. It targets plain `net10.0` and reaches the TV only through narrow
  command-target interfaces.
- `ISamsungTokenStore` keeps persistence outside protocol logic.
- `ISamsungMessageSink` receives every complete TX/RX message. The initial
  `NdjsonProtocolLogger` records sessions without interpreting away unknown data.
- `SamsungDeviceInfoClient` performs isolated, read-only HTTP(S) `/api/v2/`
  probes. It preserves raw and parsed responses without coupling REST research
  to the WebSocket transport.
- `samsungctl` owns user configuration, paths, terminal output, and process
  lifetime. The core library does not depend on the CLI.
- `SamsungController.Web` is an ASP.NET Core/Blazor Server presentation layer.
  One process-wide controller service owns the active TV connection, macro run,
  500-message protocol ring buffer, session logger, and persisted quick-access
  actions. Razor components call that service and contain no Samsung protocol
  construction logic.

The macro automation, web interface, and first data-driven menu-navigation slice
were introduced after pairing, token reuse, and remote keys were verified
against the Samsung S95F.

## Predicted menu navigation

`MenuDefinition` is a validated directed graph loaded from YAML. Nodes organize
modeled menu locations; explicit transitions contain the only key sequences the
planner may use. Anchors establish deterministic starting nodes. Draft
transitions participate in preview plans with a strong cost penalty but are
rejected by the executor. Only `verified: true` routes can reach the TV.

`MenuStateTracker` records a predicted node, reason, timestamp, and confidence
of Unknown, Low, Probable, or Synchronized. It updates after verified navigation
and observes manual/macro keys. Unmodeled navigation keys, raw requests,
cancelled execution, and partial failures invalidate prediction rather than
guessing. Successful transport sends are not treated as Samsung OSD
acknowledgements, so a planned transition yields Probable rather than
Synchronized confidence.

`MenuNavigator` owns plan and anchor execution through `IMenuCommandTarget`.
The Blazor page only requests plans and displays immutable snapshots; it does
not construct Samsung protocol messages or calculate graph paths.

## Local web boundary

The web host defaults to `http://127.0.0.1:5050`; it is not exposed on a LAN
interface. It reuses the CLI's per-user `settings.json` and `tokens.json`, while
each web process creates its own complete NDJSON session capture. The protocol
view holds only the latest 500 messages in memory and recursively redacts JSON
properties named `token` unless the user explicitly reveals sensitive values.
The on-disk capture remains complete for research and therefore must be kept
private. Raw JSON sending is gated behind a developer-mode control in the UI.

The shared layout displays connection status, saved quick-access actions, and
the predicted menu path on every page. Quick-access metadata is normalized and
persisted in `settings.json`; execution still flows through the same controller
methods as the Remote, Macros, and Menu views, so connection, automation, and
menu-recording guards remain centralized. A missing quick-access setting gets a
default `normal-video` anchor action. An explicitly saved empty list remains
empty, allowing the default to be removed intentionally.

## WebSocket handshake

The expected endpoint is:

```text
wss://HOST:8002/api/v2/channels/samsung.remote.control?name=BASE64_NAME&token=TOKEN
```

Port 8001 and `ws` are available through `--insecure`. The client name is UTF-8
encoded, then Base64-encoded and URL-escaped. The token query parameter is
omitted for first pairing.

`ClientWebSocket.ConnectAsync` only establishes the socket. A connection is not
considered authorized until an `ms.channel.connect` message arrives. On first
pairing, the TV is expected to display an authorization prompt. The CLI waits up
to the configured pairing timeout for the user to approve it.

Samsung TVs commonly use a self-signed certificate on port 8002. Certificate
relaxation is scoped to the one TV WebSocket and is enabled by default; it can be
disabled in the core connection options.

## Token lifecycle

1. Load the token by normalized host name unless a token was supplied directly.
2. Add it to the secure WebSocket URI when present.
3. Inspect `data.token` in `ms.channel.connect`.
4. Also inspect `data.clients[*].attributes.token`; this alternate location has
   been observed in the ColorControl implementation.
5. Persist a newly returned token immediately, keyed by host.
6. Reuse it on later CLI invocations and reconnect attempts.
7. Treat `ms.channel.unauthorized` as authentication failure and preserve the
   raw response in the protocol log.

The token file lives in the operating system's per-user configuration directory,
not in the repository. The CLI never prints the token.

The web profile also persists its certificate-trust choice so WebSocket and
device-information probes apply the same endpoint policy after a restart.

## Message model

`SamsungMessage` contains:

- UTC timestamp
- `TX` or `RX`
- logical channel
- parsed event/method when available
- parsed JSON payload when valid
- the complete raw JSON even when parsing fails
- parse error, if any
- connection generation

No event allowlist is applied. Unknown and malformed responses are still raised
through events, streamed to diagnostics consumers, and written to NDJSON.

## Remote commands

The initial independent payload is:

```json
{
  "method": "ms.remote.control",
  "params": {
    "Cmd": "Click",
    "DataOfCmd": "KEY_UP",
    "Option": "false",
    "TypeOfRemote": "SendRemoteKey"
  }
}
```

`Click`, `Press`, and `Release` are supported. Keys are intentionally not
restricted to a known list so experimental keys can be sent.

## Reconnect behavior

Each successful transport connection increments a generation number. When
automatic reconnect is enabled, an unexpected receive-loop termination
schedules bounded exponential reconnects and a send failure reconnects once
before retrying the complete command. Generation numbers in the session log
distinguish traffic before and after reconnect.

Automatic reconnect remains a core-client option. The local web controller
disables it: if the TV closes or loses the control channel, the shared web
session immediately becomes `Disconnected`, all command controls disable, and
the connection page offers an explicit reconnect. This prevents the UI from
presenting a stale connected session.

## ColorControl-derived assumptions

The following protocol assumptions were independently reimplemented after
reviewing ColorControl's current Samsung files:

- secure/non-secure ports are 8002/8001;
- client names are Base64-encoded in the endpoint;
- tokens are query parameters;
- pairing completes with `ms.channel.connect`;
- tokens may occur at either of the JSON paths documented above; and
- remote keys use `ms.remote.control` with the four command fields shown above.

Reference files:

- <https://github.com/Maassoft/ColorControl/blob/master/ColorControl/Services/Samsung/SamTvConnection.cs>
- <https://github.com/Maassoft/ColorControl/blob/master/ColorControl/Services/Samsung/SamsungDevice.cs>

ColorControl is GPLv3. SamsungController does not copy its types, control flow,
serialization models, or source text.

## S95F verification checklist

These items remain empirical until tested on the target TV:

- whether the S95F accepts URL-escaped Base64 padding in `name`;
- whether port 8002 remains enabled in the current firmware;
- certificate behavior and whether strict validation can ever be used;
- exact pairing prompt and timeout behavior;
- token location and whether it rotates;
- whether a newly issued token requires a second connection;
- all responses to `Click`, `Press`, and `Release`;
- whether commands receive explicit acknowledgements;
- close codes and reconnect timing when the TV sleeps or powers off; and
- unknown events emitted during menu and picture-setting changes.

Record results in `docs/research-notes.md` without generalizing a single
observation to all Samsung models or firmware versions.
