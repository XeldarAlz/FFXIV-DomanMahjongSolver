# Mahjong.Plugin.Dalamud (the Dalamud plugin)

The thin shell that runs inside FFXIV — UI windows, slash commands, addon
hooks, memory readers, action dispatch. Every other project in this repo
is consumed by this one; reverse references don't exist (the plugin layer
is the leaf, not a library).

## What's here

| Folder | Purpose |
|---|---|
| `Plugin.cs` | Dalamud entry point + MEDI composition root + lifecycle |
| `Configuration.cs` | Persisted plugin settings |
| `Adapters/` | Dalamud service → contract adapters (`DalamudEventLog`, `DalamudFrameworkScheduler`, `DalamudGameClientAdapter`) |
| `Composition/` | `PluginServices.Build()` — wires the MEDI container |
| `Actions/` | `AutoPlayLoop` (consumes `ActionStateMachine` from Mahjong.Plugin.Game), `InputDispatcher`, `HumanTiming` |
| `Commands/` | `MjAutoCommand` — slash-command dispatcher |
| `GameState/` | `AddonEmjReader`, `MahjongAddon`, `MeldTracker`, `Variants/BaseEmjVariant` (profile-driven) |
| `Hooks/` | `DiscardHook` (native ASM hook on the discard handler) |
| `Logging/` | `GameLogger`, `InputEventLogger` (NDJSON corpus building) |
| `UI/` | ImGui windows: `MainWindow`, `AboutWindow`, `DebugOverlay`, `HandOverlay`, `Theme` |

## Dependency direction

```
Mahjong.Plugin.Dalamud (this)
       │
       ├─→ Mahjong.Plugin.Game     (contracts + value types)
       ├─→ Mahjong.Policy.Abstractions
       ├─→ Mahjong.Core
       │
       ├─→ Engine                   (decomposition, shanten, scoring)
       ├─→ Policy                   (heuristic + MCTS decision making)
       └─→ Mahjong.Rules            (DomanRuleSet for live play)
```

The plugin imports every layer; no layer imports the plugin.

## Variant support

`AddonEmjReader` loads layout profiles from `layouts/*.json` (bundled as
plugin Content from [`data/layouts/`](../data/layouts/)) and constructs one
`BaseEmjVariant` per profile. Adding a new variant (JP, OC) is a JSON file
— no code change. See [`Mahjong.Plugin.Game/README.md`](../Mahjong.Plugin.Game/README.md).

## Status

- **Phase 7.A delivered**: MEDI container + 3 Dalamud adapters
  (`DalamudEventLog`, `DalamudFrameworkScheduler`, `DalamudGameClientAdapter`).
  Policies resolve through DI.
- **Phase 7.B partially delivered**: `ActionStateMachine` extracted from
  `AutoPlayLoop`.
- **Phase 7.B owed**: project rename (→ `Mahjong.Plugin.Dalamud`),
  immutable `Configuration` + `IConfigMigrator`, `DiscardHook` →
  `IDiscardCapture` strategies, mass-migration of static `Plugin.X`
  accesses to constructor injection, ~150 mock-adapter tests.

See [`docs/architecture.md`](../docs/architecture.md) for the layered
overview and extension points.
