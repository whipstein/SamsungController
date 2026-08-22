# Samsung/Tizen research notes

Do not record a conclusion without the request, response, environment, observed
result, repeatability, and risk. Session logs can contain pairing tokens and
should not be committed.

## Observation template

```text
Date/time (UTC):
TV model:
Firmware:
Input/source:
SDR or HDR:
Picture mode:
API/channel:
Request:
Response:
Observed result:
Repeatability:
Risk: Normal | Advanced | Service | Experimental | Potentially destructive
Notes:
```

## S95F validation queue

- Pair on port 8002 with no token.
- Verify token persistence and reconnect in a new process.
- Capture the response to each `Click`, `Press`, and `Release` action.
- Capture unsolicited events while opening and closing the settings OSD.
- Compare `/api/v2/` state while powered on, in standby, and changing apps.
- Query/read before attempting any undocumented write.

No service-menu or undocumented write is safe for automatic execution.

## 2026-08-22 — initial S95F transport validation

```text
Date/time (UTC): 2026-08-22
TV model: Samsung S95F
Firmware: Not recorded
Input/source: Not recorded
SDR or HDR: Not recorded
Picture mode: Not recorded
API/channel: wss://TV:8002/api/v2/channels/samsung.remote.control
Observed result:
  - Initial socket reached the TV but returned ms.channel.timeOut because the
    approval dialog was missed.
  - Retrying and selecting Allow returned ms.channel.connect with a token.
  - A later process reused the saved token without another approval prompt.
  - Click remote-key commands moved the TV OSD as expected.
  - A passive listener received ms.channel.connect and no later unsolicited
    traffic during the observation window.
Repeatability: Pairing/token reuse and key commands were confirmed by the user.
Risk: Normal
Notes: No conclusion yet about application-query support or physical-remote/OSD
event availability beyond this short observation.
```
