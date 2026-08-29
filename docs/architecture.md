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
  atomic YAML serialization, behavioral verification metadata, plan expansion,
  cancellation, and progress events, plus menu-definition
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
transitions are confined to the Build & Verify workflow. The everyday Menu page
lists and runs only `verified: true` destinations, anchors, and transitions.
Selecting a destination creates and executes its verified plan as one controller
operation; the separate planning controls remain available for route inspection.
Only verified routes can reach the TV from that page.

`MenuStateTracker` records a predicted node, reason, timestamp, and confidence
of Unknown, Low, Probable, or Synchronized. It updates after verified navigation
and observes manual/macro keys. Unmodeled navigation keys, raw requests,
cancelled execution, and partial failures invalidate prediction rather than
guessing. Successful transport sends are not treated as Samsung OSD
acknowledgements, so a planned transition yields Probable rather than
Synchronized confidence.

`MenuNavigator` owns plan and anchor execution through `IMenuCommandTarget`.
The Blazor page requests navigation actions and displays immutable snapshots;
it does not construct Samsung protocol messages or calculate graph paths.

After the web controller completes a connection, it runs one verified anchor to
establish a known initial state, preferring an anchor that targets
`normal-video`. This connection-time synchronization does not reintroduce
implicit reset commands before later route, timing, return-script, or draft
validation execution.

## Local web boundary

The web host defaults to `http://127.0.0.1:5050`; it is not exposed on a LAN
interface. It reuses the CLI's per-user `settings.json` and `tokens.json`, while
each web process creates its own complete NDJSON session capture. The protocol
view holds only the latest 500 messages in memory and recursively redacts JSON
properties named `token` unless the user explicitly reveals sensitive values.
The on-disk capture remains complete for research and therefore must be kept
private. Raw JSON sending is gated behind a developer-mode control in the UI.

The shared layout displays connection status, saved quick-access actions, and
the predicted menu's leaf label in large type on every page; the full path and
confidence remain available as hover/accessibility context. Quick-access
metadata is normalized and persisted in `settings.json`; execution still flows
through the same controller methods as the Remote, Macros, and Menu views, so
connection, automation, and menu-recording guards remain centralized. A missing
quick-access setting gets a default `normal-video` anchor action. An explicitly
saved empty list remains empty, allowing the default to be removed intentionally.

The macro studio treats syntax validation and behavioral verification as
separate states. `MacroCatalogWriter` validates and atomically replaces the
normalized catalog. Each definition persists an independent pass count; only a
successful replay of that same macro can be confirmed, and the third accepted
run promotes it to verified. Behavioral edits or a reported failed replay reset
the count and remove the macro from quick access. Description-only edits and
renames preserve verification, with renames updating nested calls and pinned
targets.

Each detailed macro may declare a starting menu node ID, and macro steps may
also contain verified menu destinations. The executor expands both starts and
menu calls as first-class operations and requires an `IMacroMenuCommandTarget`;
capability and static node checks happen before the first macro key is sent. A
start is inserted before every root or nested macro invocation. The web target
uses `MenuNavigator.PrepareStateAsync`: it selects a normal verified route from
a known state, executes the least-cost verified anchor plus route from an
unknown state, or sends nothing when already at the target. The macro retains
exclusive ownership of automation throughout. CLI targets intentionally lack
that menu context and reject macros containing starts or menu calls before
execution begins.

Build & Verify persists menu-tree nodes before route recording, allowing display
names and parents to be edited while keeping stable node IDs for route references.
The YAML node sequence stores custom sibling order; moving a sibling rewrites the
tree in depth-first order while preserving its descendants, and alphabetical
display does not mutate the definition. Unreferenced draft branches can be deleted;
referenced branches must first have their traversal, anchor, or return definition
removed.

Build & Verify may delete an incorrect verified setting after a two-step UI
confirmation. Deletion removes the setting's full descendant subtree and every
transition that references it, and clears a return strategy whose menu root was
removed. Verified anchor targets are protected so connection-time
synchronization always retains a known-state foundation.

Build & Verify tracks the attention state of its collapsible return-script,
system-timing, and draft-validation sections. Return-script resolution gives a
verified exact-node override priority, then falls through to the verified
target transition's verified integrated `returnSteps`, the verified
menu-root/deeper scripts, and the anchor's default operations. Integrated return
keys remain on the same transition as its forward keys: Prepare source exercises
the return keys, Replay test exercises the forward keys, and one validation
session verifies the pair. Partial counts are keyed by draft item, so alternating
between commands does not reset either command's progress. Accepting any replay
pass explicitly synchronizes the state tracker to its target. That exact
synchronized state may use its draft integrated return before 3/3, while a
merely predicted draft state may not. The third acceptance persists verification
and immediately executes the normal-video anchor unless the verified item already
targets normal video.
Legacy separate `return-to-video-replacement` anchors are folded into their
target transition when the definition is loaded. A user may keep a section
minimized while its state is unchanged; a transition from
verified to needing validation, or from no drafts to pending drafts,
automatically expands the affected section.

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
to the configured pairing timeout for the user to approve it. After authorization,
the connection enters `Warming` for the configured post-connect delay before it
becomes available to command senders.

The transport configures both `KeepAliveInterval` and `KeepAliveTimeout`. On
.NET 9 and later this selects bidirectional WebSocket PING/PONG health checking,
so an unresponsive TV is detected without waiting for an application-level
message.

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
schedules bounded exponential reconnects. A send failure may mean that the TV
received the complete key before the socket reported its error, so the client
refreshes the connection but never retries that ambiguous command. Generation
numbers in the session log distinguish traffic before and after reconnect.

Only one command may wait for connection readiness. After the configured idle
threshold, that command forces a fresh token-authenticated channel, waits through
`Warming`, and is then transmitted exactly once. This refresh sends no hidden TV
keys. A zero idle threshold disables proactive refresh.

Automatic reconnect remains a core-client option. The local web controller
disables it: if the TV closes or loses the control channel, the shared web
session immediately becomes `Disconnected`, all command controls disable, and
the connection page offers an explicit reconnect. PING/PONG makes that transition
timely, while the idle refresh handles a locally open but application-cold channel
before its next command.

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
