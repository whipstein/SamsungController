# Display definitions

A display definition is the small user-facing layer above reusable menu
definitions. It remembers one physical display's friendly name and connection
defaults, then links that display to one or more menu definitions and their
menu-layout configurations.

Selecting one display on **Connection** can therefore restore all of these at
once:

- display name and optional IP address;
- secure/non-secure connection, port override, certificate behavior, and saved channel-readiness settings;
- the default menu definition; and
- the menu configuration used for that definition.

It does not copy menu topology, key timing, calibration values, pairing tokens, or
verification evidence. Use **Files and connection details → Reload menu file** on Connection after editing the
referenced file outside the app; this reparses the same file rather than making
a copy. Display Verification continues to use the menu
definition ID plus model, firmware, signal, picture mode, and input/source.
Creating, editing, or selecting a display definition does not change those
fingerprints or their `menu-verifications` sidecar.

## Create one in the interface

1. Open **Connection** while disconnected.
2. Select **Create new combination**. The default view shows only saved combinations.
3. In **Display**, enter the display name and IP address.
4. In **Menu**, choose an existing menu definition and layout, or **Define a new menu**
   with the model, firmware, and optional signal/picture-mode/input context.
5. Keep the recommended **Connection settings**, or expand **Advanced connection
   settings** to change them. The optional **Display file ID** is generated from
   the display name when blank.
6. Select **Save combination**. When defining a new menu, **Save & define menu**
   opens Build & Verify to enter its tree next. **Cancel** discards the draft
   without changing the active display or menu.

The app writes `<id>.display.json` under the per-user configuration directory's
`display-definitions/` folder. If that ID already exists, it asks before
replacing the user file.

To associate another menu or layout with the same physical display, choose its
saved combination, select **Edit combination**, choose the other menu/layout,
and select **Save changes**. The new reference is added and becomes the file's
default. Every linked context appears as a separate choice under **Use saved
combination**. This is useful
for separate SDR, HDR, input, picture-mode, or settings-dependent menu files.

Selecting a saved combination loads that exact menu/layout for the display.
Disconnect before switching combinations or editing connection settings.

## Discovery locations

The Connection page discovers `*.display.json` files recursively in:

1. the per-user `display-definitions/` directory;
2. the repository's `display-definitions/` directory when running from source;
3. the installed application's `display-definitions/` directory; and
4. the explicitly active custom file, if one is outside those catalogs.

Invalid files appear under **Files and connection details** with their diagnostic;
only usable combinations appear in the main picker. JSON syntax and unknown-field
errors include a one-based line and column. Use **Refresh list** to rescan.

## Version 1 format

```json
{
  "version": 1,
  "id": "living-room-s95f",
  "name": "Living Room S95F",
  "connection": {
    "host": "192.0.2.10",
    "secure": true,
    "port": null,
    "allowUntrustedCertificate": true
  },
  "defaultMenu": "sdr-filmmaker",
  "menus": [
    {
      "id": "sdr-filmmaker",
      "definitionId": "s95f-1296-sdr",
      "source": "repository",
      "path": null,
      "configurationId": "default"
    },
    {
      "id": "hdr-filmmaker",
      "definitionId": "s95f-1296-hdr",
      "source": "userData",
      "path": "../menu-definitions/s95f-1296-hdr.json",
      "configurationId": "default"
    }
  ]
}
```

Fields:

- `version` must be `1`.
- `id` is the stable display ID and filename stem.
- `name` is the label shown in the app and Samsung pairing request.
- `connection.host` is optional in a shareable template. When omitted, selecting
  the definition preserves the address already entered locally.
- `connection.secure` defaults to `true`.
- `connection.port` is `null` for automatic port 8002/8001 selection, or an
  explicit value from 1 through 65535.
- `connection.allowUntrustedCertificate` should normally remain `true` for a
  Samsung TV's local certificate.
- `connection.keepAliveIntervalSeconds` is optional (5–120 seconds).
- `connection.keepAliveTimeoutSeconds` is optional (0 to disable, or 2–60 seconds;
  it must not exceed the interval).
- `connection.postConnectWarmupMilliseconds` is optional (0–10000 ms).
- `connection.reconnectAfterIdleSeconds` is optional (0 to disable, or 30–3600 seconds).
  The setup form saves all four readiness values with the display; definitions
  that omit them retain the current local values when selected.
- `defaultMenu` names the menu-reference ID loaded when the display is selected.
  When omitted, the first entry is used.
- `menus` contains one or more references. Reference IDs must be unique.
- `definitionId` must equal the referenced menu definition's root `id`.
- `configurationId` is optional. When omitted, the menu's first configuration
  is used.

## Menu source and path resolution

`source` controls which copy wins when the same menu-definition ID exists in
more than one catalog:

| Value | Resolution |
| --- | --- |
| `any` | User data first, then repository, installation, and custom files. |
| `userData` | Only the per-user menu-definition catalog. |
| `repository` | Only the source repository catalog. |
| `installation` | Only menu definitions shipped beside the application. |
| `customFile` | Requires `path`; useful for an external file. |

When `path` is present, it takes precedence over catalog resolution. Relative
paths are resolved from the display-definition file. The web editor writes a
portable relative path for a user-data menu, source-only references for
repository/installation menus, and an absolute path only for an external custom
file.

## Sharing and privacy

Repository display definitions are included in packaged releases. Personal
display definitions stay under user data. An IP address is not a pairing token,
but it is still private network information; remove `connection.host` before
publishing a generic display definition. Tokens remain only in the local
host-keyed `tokens.json` file and are never written into a display definition.
