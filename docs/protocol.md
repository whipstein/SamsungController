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

The second event is `ed.installedApp.get`. Support and response shapes are
firmware-dependent and remain to be verified on the S95F. These envelopes are
treated as experimental protocol behavior even though their names imply reads.

The envelope was independently implemented after checking the behavior in the
[samsungctl WebSocket implementation](https://github.com/roberodin/ha-samsungtv-custom/blob/master/custom_components/samsungtv_custom/samsungctl_080b/remote_websocket.py).

## Session recordings

The CLI writes newline-delimited JSON. Each record has a timestamp, direction,
channel, event/method, parsed payload (when valid), exact raw JSON, parse error
(when invalid), and connection generation. Tokens may appear in pairing RX
messages, so session logs are sensitive and must not be committed.

## Sources and licensing

The endpoint and message assumptions were checked against the current
[ColorControl Samsung implementation](https://github.com/Maassoft/ColorControl/tree/master/ColorControl/Services/Samsung),
used as a behavioral reference under its GPLv3 license. This project is a clean,
independent implementation.
