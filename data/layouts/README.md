# Mahjong addon layout profiles

Each `*.json` file describes one client-variant of FFXIV's Mahjong addon —
the memory offsets, node IDs, AtkValue indices, state codes, and texture
base the plugin needs to read game state from a live `AtkUnitBase*`.

## Adding a new variant (JP, OC, ...)

1. Have an in-game capture from the new client. The plugin's
   `/mjauto variant dump` command writes a layout-snapshot file you can diff
   against `emj.json` / `emj_l.json` to find divergences.
2. Drop a new `<variant>.json` in this folder, copying the closest existing
   profile and overriding the divergent fields.
3. Register the profile in the plugin's variant-selector wiring (one line).
4. The plugin picks it up on next load — no code changes to
   `Mahjong.Plugin.Game`.

## Format

| Field | Type | Notes |
|---|---|---|
| `name` | string | Display label, e.g. `"EmjL"`. |
| `addonName` | string | The addon name as exposed to Dalamud (used as a tiebreaker when probes are inconclusive). |
| `tileTextureBase` | int | Hand-tile texture id offset. Subtract this from the int32 at `offsets.handArrayStart + i*4` to recover the 0–33 tile id. |
| `offsets.*` | int (decimal) or hex string `"0x0500"` | Byte offsets into `AtkUnitBase*`. |
| `nodeIds.*` | uint | `GetNodeById` keys for the call-modal host and inner shell. |
| `atkValues.*` | int | Indices into `unit->AtkValues`. |
| `stateCodes.*` | int | Magic numbers for the state machine. |
| `limits.*` | int | Plausibility bounds — reads outside these get rejected. |

Numeric fields accept hex strings (e.g. `"0x0DB8"`) so the bit-level
structure stays readable. The loader strips `0x` and parses as base 16.

## Why riichi rules elsewhere, but Doman client here

The plugin runs against the FFXIV Doman Mahjong addon, which is a Doman
variant. These layout profiles are **structural** (offsets, node IDs) — the
*ruleset* (yaku list, scoring) is a separate concern handled by
`Mahjong.Rules`. See `docs/ruleset.md`.
