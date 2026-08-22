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
