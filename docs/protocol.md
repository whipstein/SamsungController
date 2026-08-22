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

## Remote key requests

Method: `ms.remote.control`

Parameters:

| Field | Value |
| --- | --- |
| `Cmd` | `Click`, `Press`, or `Release` |
| `DataOfCmd` | Samsung key string |
| `Option` | `"false"` |
| `TypeOfRemote` | `SendRemoteKey` |

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
