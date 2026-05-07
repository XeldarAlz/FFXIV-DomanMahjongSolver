# Architecture

How the codebase is laid out, what each project owns, and how to extend it.

## Layered overview

```
                         ┌─────────────────────────┐
                         │      Dalamud (host)      │
                         └──────────────┬──────────┘
                                        │
                         ┌──────────────▼──────────┐
                         │  Mahjong.Plugin.Dalamud (plugin) │  thin shell —
                         │  - Plugin.cs (DI root)   │  Dalamud lives here
                         │  - Adapters/             │  and only here
                         │  - UI / commands / hooks │
                         └──────────────┬──────────┘
                                        │
        ┌───────────────────────────────┼───────────────────────────┐
        │                               │                           │
   ┌────▼─────────────┐  ┌──────────────▼────┐  ┌────────────────────▼──┐
   │ Mahjong.Plugin   │  │ Policy            │  │ Mahjong.Replay        │
   │ .Game            │  │ - Heuristic*Policy│  │ - TenhouLog           │
   │ - LayoutProfile  │  │ - Mcts            │  │ - TenhouReplay        │
   │ - Result<T,E>    │  │ - Tuning          │  │ - GoldenFileHarness   │
   │ - 8 contracts    │  │ - Simulator       │  │                       │
   │ - ActionStateM/c │  │                   │  │                       │
   └─────────────┬────┘  └─────────┬─────────┘  └──────────┬────────────┘
                 │                  │                       │
                 └──────────┬───────┴───────────┬──────────┘
                            │                   │
              ┌─────────────▼────┐    ┌─────────▼────────────────────┐
              │ Mahjong.Policy   │    │ Engine                       │
              │ .Abstractions    │    │ - HandDecomposer             │
              │ - IPolicy + 6 sub│    │ - ShantenCalculator          │
              │ - IRandomSource  │    │ - UkeireEnumerator           │
              │ - IWeightProvider│    │ - Scorer (consumes IRuleSet) │
              │ - WeightBundle   │    │                              │
              └──────────┬───────┘    └─────────────┬────────────────┘
                         │                          │
                         └────────────┬─────────────┘
                                      │
                         ┌────────────▼──────────┐
                         │ Mahjong.Rules          │
                         │ - IRuleSet             │
                         │ - 38 IYakuRule         │
                         │ - DomanRuleSet         │
                         │ - RiichiRuleSet        │
                         │ - Standard{Scoring,    │
                         │   Dora,Fu}Rule         │
                         └────────────┬──────────┘
                                      │
                         ┌────────────▼──────────┐
                         │ Mahjong.Core           │
                         │ - Tile, Wind, Seat     │
                         │ - Meld, Hand, Wall     │
                         │ - Decomposition        │
                         │ - WinContext, Yaku     │
                         │ - StateSnapshot, ...   │
                         └────────────────────────┘
```

**Dependency rule**: arrows go down. The plugin (top) knows everything below
it; `Mahjong.Core` (bottom) knows nothing above it. **Dalamud only enters the
graph at the plugin layer** — every other project compiles and tests without
it.

## Project status

| Project | Tests | Purpose |
|---|---|---|
| Mahjong.Core | 58 | Pure value types — the language |
| Mahjong.Rules | 51 | Pluggable rules — yaku, scoring, dora, fu |
| Mahjong.Policy.Abstractions | — | Interfaces only |
| Mahjong.Plugin.Game | 33 | Plugin-layer contracts + variant data |
| Mahjong.Replay | 17 | Tenhou parsing + golden-file regression |
| Engine | 113 | Decomposition, shanten, ukeire, scoring orchestration |
| Policy | 77 | Heuristic + MCTS decision implementations |
| Tuner | — | Console exe for offline weight optimization |
| Mahjong.Plugin.Dalamud | — | The Dalamud plugin (thin shell) |

Total: **349 automated tests**, all green.

## Adding things

### A new client variant (JP, OC, ...)

1. Capture in-game offsets from the new client (run `/mjauto variant dump`).
2. Drop a new file at `data/layouts/<variant>.json` — copy the closest
   existing profile and override divergent fields.
3. The plugin auto-discovers it on next launch via
   `JsonLayoutProfileLoader.LoadAll(...)`.

**Zero code changes needed.** See
[`data/layouts/README.md`](../data/layouts/README.md) for the JSON schema.

### A new yaku

1. Drop a new `IYakuRule` implementation under
   `Mahjong.Rules/YakuRules/<Name>Rule.cs` (or `Yakuman/<Name>Rule.cs`).
   Each rule is ~30 LOC: a `YakuDefinition` + a `Detect` method + an
   optional `Conflicts` list.
2. Register it in `RiichiRuleSet.YakuRules` and/or `DomanRuleSet.YakuRules`.
3. Add a focused unit test in `tests/Mahjong.Rules.Tests/YakuRuleTests.cs`.

The orchestrator (`Engine.Scorer`) iterates the registered rules — no
imperative branching to update.

### A new policy / sub-policy

1. Implement `IPolicy` (top-level) or one of the sub-policy interfaces
   (`IDiscardPolicy`, `ICallPolicy`, `IRiichiPolicy`, `IPushFoldPolicy`,
   `IPlacementPolicy`, `IRolloutPolicy`) under `Policy/`.
2. Register the new implementation in
   `Mahjong.Plugin.Dalamud/Composition/PluginServices.cs`.
3. Add a unit test under `tests/Policy.Tests/` — every sub-policy is
   constructor-injected and testable in isolation.

### A new weight bundle

1. Edit `Mahjong.Policy.Abstractions/Weights/WeightBundle.cs` to add the
   sub-record (or new field on an existing one). Bump
   `WeightBundle.CurrentSchemaVersion`.
2. Run the Tuner — outputs `data/weights/{evo|coord}-{timestamp}.json`.
3. Wire the new field into the consuming policy class (constructor
   injection via `IWeightProvider.Current`).

`JsonWeightProvider.Load` rejects schema-version mismatches with a clear
error — silent migration is worse than a loud failure when weights drift.

### A new Tenhou replay regression fixture

1. Save a Tenhou log as `data/replays/<descriptive-name>.tenhou.json`.
2. Run the test suite once with `UPDATE_REPLAY_SNAPSHOTS=1` set; the
   harness generates `<descriptive-name>.snapshot.json` next to it.
3. Inspect the snapshot. Commit both files together.

Subsequent runs compare against the snapshot. Behavior changes show up as
test failures with reviewable JSON diffs.

## Extending in production

Every extension point above is **data first**. Code changes in the contract
layers (`Mahjong.Core`, `Mahjong.Policy.Abstractions`) are rare; nearly all
new feature work lands as new files in already-data-driven folders
(`data/layouts/`, `Mahjong.Rules/YakuRules/`, `Policy/`,
`data/replays/`, `data/weights/`).

## Cross-cutting concerns

| Concern | Where |
|---|---|
| Logging | `IEventLog` (Mahjong.Plugin.Game) → `DalamudEventLog` adapter |
| Threading | `IFrameworkScheduler` → `DalamudFrameworkScheduler` |
| Randomness | `IRandomSource` → `SeededRandomSource` (deterministic with seed) |
| Configuration | `Configuration.cs` (mutable, Phase 7.B owes immutable + migration) |
| DI container | MEDI in `Mahjong.Plugin.Dalamud/Composition/PluginServices.cs` |
| Versioning | `Directory.Build.props` `<Version>` → repo.json (CI sync gate) |
| Style | `.editorconfig` (warnings-as-errors via `Directory.Build.props`) |
| Determinism | `Directory.Build.props` sets `ContinuousIntegrationBuild=true` under CI |

## Reading order for new contributors

1. `README.md` (root) — what the plugin does
2. This doc — how it's laid out
3. `docs/ruleset.md` — the rules spec (Doman vs Riichi delta)
4. `Mahjong.Core/README.md` — the vocabulary
5. The project README of the layer you're working on
