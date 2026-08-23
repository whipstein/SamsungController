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
- Record firmware, input, SDR/HDR, and picture mode before mapping each menu transition.
- Verify the exact highlighted item after `KEY_MENU` opens the S95F settings surface.
- Map one repeatable route from the settings surface to Picture, then Expert Settings.
- Compare `/api/v2/` state while powered on, in standby, and changing apps.
- Send `ed.edenApp.get` and `ed.installedApp.get` from the Protocol page and
  record the exact S95F response or absence of a response.
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

## 2026-08-22 — S95F firmware 1296 menu traversal

```text
Date/time (UTC): 2026-08-22 (exact time not recorded)
TV model: Samsung S95F
Firmware: 1296
Input/source: Home Theater System
SDR or HDR: SDR
Picture mode: Filmmaker Mode
API/channel: wss://TV:8002/api/v2/channels/samsung.remote.control
Request:
  - Anchor: KEY_RETURN x3
  - Normal video to Picture: KEY_MENU, KEY_DOWN, KEY_ENTER
  - Picture to Expert Settings: KEY_DOWN x4, KEY_ENTER
Response: No menu-position response was available; the user visually confirmed the OSD.
Observed result:
  - The Return anchor removed the OSD.
  - KEY_MENU opened Settings with the last modified command highlighted.
  - KEY_DOWN followed by KEY_ENTER opened Picture.
  - Four KEY_DOWN clicks followed by KEY_ENTER opened Expert Settings.
  - One Return click closed Settings from the modeled settings surface.
  - Two KEY_MENU clicks from within the menu system returned to video.
Repeatability: Anchor and Settings open/close passed 3 out of 3. The full route
  through Picture to Expert Settings passed 3 out of 3. Two KEY_MENU clicks
  from Expert Settings returned to video 3 out of 3.
Risk: Normal
Notes: Picture, Expert Settings, and the Expert Settings exit route are verified
  for the recorded firmware and viewing context.
```
