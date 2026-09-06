# Distributable menu structures

This directory is the repository catalog for reusable Samsung TV menu
structures. Add one YAML or JSON file per model/firmware topology. Files placed
here are copied into packaged releases automatically.

A distributable structure contains:

- the nested on-screen menu tree;
- control types, options, slider bounds, and conditional visibility rules;
- declared external equipment states, such as an RGB/YCbCr generator mode;
- navigation anchors, return strategies, routes, and timing definitions;
- named menu configurations when settings change the visible topology.

It must not contain a `verification` block, connection details, tokens, protocol
logs, macros, or saved setting values from a particular TV. SamsungController
stores display-bound verification under the user's local
`menu-verifications/` directory and named current-TV states in local
`settings.json`.

From **Build & Verify**, use **Export & use structure** and choose a path in this
directory. The exported copy becomes the active authoring source. Review the
resulting topology before committing it. Use a stable,
descriptive filename such as `s95f-1296.yaml`; add signal, picture mode, or input
only when those conditions genuinely require a different topology.

The generic authoring tutorial remains in `samples/menus/menu.example.yaml`.

## Included structures

| File | Display context | Notes |
| --- | --- | --- |
| `odyssey_g9_oled-game.json` | Samsung Odyssey G9 OLED, firmware 2231, Game signal | Nested game-mode topology. Picture mode and input remain `any`; complete local Display Verification before using Menu controls. |
| `s95f-1296-sdr.json` | Samsung S95F, firmware 1296, SDR | Complete nested base topology and generated routes. Picture mode and input remain `any`; select and complete local Display Verification before using Menu controls. |

The S95F file is the distributable structure only. Its display-verification
sidecar, pairing token, protocol logs, macros, and saved current/target values
remain outside the repository.
