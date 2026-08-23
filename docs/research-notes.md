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
- Compare `/api/v2/` state in standby; all six powered-on menu-position and
  Home/Netflix/Hulu captures produced the same normalized payload.
- Repeat `ed.edenApp.get` and `ed.installedApp.get` while changing app or power
  state; the first connected-session attempt produced no observable response.
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

## 2026-08-23 — application query response check

```text
Date/time (UTC): 2026-08-23 (exact time not recorded)
TV model: Samsung S95F
Firmware: 1296 (assumed unchanged from the previous observation; not reconfirmed)
Input/source: Not reconfirmed
SDR or HDR: Not reconfirmed
Picture mode: Not reconfirmed
API/channel: wss://TV:8002/api/v2/channels/samsung.remote.control
Request:
  - ms.channel.emit with event ed.edenApp.get, data "", to "host"
  - ms.channel.emit with event ed.installedApp.get, data "", to "host"
Response: No RX response was observed for either query in the Protocol capture.
Observed result:
  - Both TX requests were sent from the Protocol page.
  - Neither produced an observable response during the user's observation window.
Repeatability: One reported attempt per query.
Risk: Experimental (read/query only)
Notes: This establishes only that no response was observed in this session. It
  does not establish that the events are unsupported in all S95F states,
  firmware versions, channels, or request variants.
```

## 2026-08-23 — powered-on device information across menu positions

```text
Date/time (UTC): 2026-08-23 15:14:39–15:17:04
TV model: Samsung S95F; API modelName QN65S95FAFXZA
Firmware: 1296 (assumed unchanged from the previous observation; the API
  reported firmwareVersion "Unknown")
Input/source: Not reconfirmed
SDR or HDR: Not reconfirmed
Picture mode: Not reconfirmed
API/channel: HTTPS GET /api/v2/ on port 8002
Request:
  - Label: Powered on - Home
  - Label: Powered on - Contrast
  - Label: Powered on - White Balance 20 pt Percentage Select
Response: HTTP 200 with a JSON device-information document for every request.
Observed result:
  - All three normalized response payloads were identical.
  - The payload also matched all three earlier powered-on menu-position captures.
  - device.PowerState was "on" in all three conditions.
  - The response identified Tizen, a 3840x2160 display, wired networking,
    TokenAuthSupport, EDEN availability, remote availability, and API version
    2.0.25.
  - No field identified the current OSD menu, selected picture control, or
    picture-control value.
Repeatability: One capture at each of three powered-on menu positions.
Risk: Experimental (read/query only)
Notes: Unique device identifiers, MAC address, and LAN address from the source
  capture are intentionally omitted. This result applies only to the three
  observed menu positions; application changes are recorded separately below,
  and standby remains untested.
```

## 2026-08-23 — powered-on device information across applications

```text
Date/time (UTC): 2026-08-23 15:20:35–15:21:35
TV model: Samsung S95F; API modelName QN65S95FAFXZA
Firmware: 1296 (assumed unchanged from the previous observation; the API
  reported firmwareVersion "Unknown")
Input/source: Not reconfirmed
SDR or HDR: Not reconfirmed
Picture mode: Not reconfirmed
API/channel: HTTPS GET /api/v2/ on port 8002
Request:
  - Label: Powered on - Home
  - Label: Powered on - Netflix
  - Label: Powered on - Hulu
Response: HTTP 200 with a JSON device-information document for every request.
Observed result:
  - All three normalized response payloads were identical.
  - device.PowerState was "on" in all three conditions.
  - No field identified Home, Netflix, Hulu, or another current application.
  - The isSupport string reported EDEN_available as "true", but this endpoint
    did not expose an active-app field in the observed responses.
Repeatability: One capture in each of three powered-on application conditions.
Risk: Experimental (read/query only)
Notes: Unique device identifiers, MAC address, and LAN address from the source
  capture are intentionally omitted. This result does not establish that all
  Samsung endpoints omit active-application state. Standby remains untested.
```
