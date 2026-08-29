# Samsung protocol notes

SamsungController 0.1 uses the modern Samsung/Tizen WebSocket remote-control
channel. This is an empirical, undocumented interface; all behavior should be
validated against the target television and firmware.

## Endpoints

| Mode | Endpoint |
| --- | --- |
| Secure (default) | `wss://HOST:8002/api/v2/channels/samsung.remote.control` |
| Non-secure | `ws://HOST:8001/api/v2/channels/samsung.remote.control` |

Query parameters:

- `name`: Base64-encoded UTF-8 application name.
- `token`: previously paired token, when available.

## Pairing responses

The expected success event is `ms.channel.connect`. The client preserves the
entire response and looks for a token at either:

```text
data.token
data.clients[*].attributes.token
```

`ms.channel.unauthorized` is treated as an authentication error. Every other
event remains observable and is not discarded.

`ms.channel.timeOut` means the TV ended the authorization window without
granting access. The client reports this as a pairing timeout and does not start
an automatic reconnect loop for a connection that was never authorized.

## Remote key requests

Method: `ms.remote.control`

Parameters:

| Field | Value |
| --- | --- |
| `Cmd` | `Click`, `Press`, or `Release` |
| `DataOfCmd` | Samsung key string |
| `Option` | `"false"` |
| `TypeOfRemote` | `SendRemoteKey` |

## Application queries

The interactive console and the Protocol page's **Research queries** panel
provide two known read/query events using the `ms.channel.emit` envelope:

```json
{
  "method": "ms.channel.emit",
  "params": {
    "data": "",
    "event": "ed.edenApp.get",
    "to": "host"
  }
}
```

The second event is `ed.installedApp.get`. Support, response presence, and
response shapes remain model-, firmware-, and state-dependent hypotheses.
These envelopes are treated as experimental protocol behavior even though their
names imply reads.

The envelope was independently implemented after checking the behavior in the
[samsungctl WebSocket implementation](https://github.com/roberodin/ha-samsungtv-custom/blob/master/custom_components/samsungtv_custom/samsungctl_080b/remote_websocket.py).

## Device-information probes

The Protocol page can issue a read-only `GET /api/v2/` request against the
configured TV endpoint. Secure profiles use HTTPS and port 8002 by default;
non-secure profiles use HTTP and port 8001. A configured port override is
honored, and acceptance of the TV certificate follows the saved connection
profile.

Each probe has a user-entered state label such as `Powered on`, `Streaming app SDR`,
or `Standby`. Up to 20 observations remain available for comparison and JSON
export in the running web session. Successful HTTP responses are also added to
the protocol stream and NDJSON capture with their exact response body. Network
failures are retained as observations, which allows a no-response standby test
to be distinguished from a probe that was never attempted.

The response is diagnostic data only. SamsungController does not infer a
current application, power state, or supported feature until those fields have
been validated on the target TV.

## Session recordings

The CLI writes newline-delimited JSON. Each record has a timestamp, direction,
channel, event/method, parsed payload (when valid), exact raw JSON, parse error
(when invalid), and connection generation. Tokens may appear in pairing RX
messages, so session logs are sensitive and must not be committed.

The Protocol page redacts pairing tokens separately from device UUIDs, MAC
addresses, and IP addresses. Both categories are hidden by default in the live
view, copies, and exports, and each has its own explicit reveal checkbox. The
on-disk NDJSON capture remains exact and unredacted for research.

## Sources and licensing

The endpoint and message assumptions were checked against the current
[ColorControl Samsung implementation](https://github.com/Maassoft/ColorControl/tree/master/ColorControl/Services/Samsung),
used as a behavioral reference under its GPLv3 license. This project is a clean,
independent implementation.
