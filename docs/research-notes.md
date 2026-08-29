# Samsung/Tizen research template

This tracked document is intentionally generic. Keep observations from a real
TV in `user-data/research-notes.md`, which Git ignores, unless a sanitized result
is deliberately prepared for publication.

Do not commit pairing tokens, raw session logs, LAN addresses, UUIDs, MAC
addresses, device names, serial numbers, account information, or application
history. Use the Protocol page's redacted export as a starting point and review
the result manually before sharing it.

## Observation template

```text
Date/time (UTC):
TV model or generic family:
Firmware:
Input/source:
SDR or HDR:
Picture mode:
API/channel:
Request:
Response (redacted):
Observed result:
Repeatability:
Risk: Normal | Advanced | Service | Experimental | Potentially destructive
Notes:
```

## Validation queue template

- Confirm pairing and token reuse without recording the token.
- Record the environment before testing menu traversal.
- Verify remote Click, Press, and Release behavior independently.
- Capture only redacted responses to read-only queries.
- Build menu routes in the browser and require three visual passes.
- Keep service-menu and undocumented write experiments out of normal workflows.

An observation from one TV, firmware, input, or picture mode must not be
generalized to other devices without independent verification.
