# Mahjong.Plugin.Game

Dalamud-free contracts and data for the FFXIV plugin layer. The
implementation (memory reading, hooks, UI) lives in `Mahjong.Plugin.Dalamud`; this
project is the pure-logic seam between them.

## Contains

- **Pure value types** — `Result<T, E>`, `ReadError`, `DispatchContext`,
  `ActionStateMachine`.
- **Contract interfaces** — `IGameClientAdapter`, `IFrameworkScheduler`,
  `IAddonReader`, `IActionDispatcher` + `DispatchResult`,
  `IConfigService<TConfig>`, `IEventLog` + `EventLevel`,
  `IDiscardCapture` + `HookHealth` + `DiscardEvent`, `IMeldRecorder`.
- **Variant data** — `LayoutProfile` record + `JsonLayoutProfileLoader` +
  `IVariantStrategy` / `IVariantSelector`.

## Variant data flow

Layout JSONs live in [`data/layouts/`](../data/layouts/). Each describes one
client variant's memory offsets, node IDs, AtkValue indices, state codes,
and texture base.

```
data/layouts/emj.json     ───┐
data/layouts/emj_l.json   ───┤   JsonLayoutProfileLoader.LoadAll()
data/layouts/<new>.json   ───┘             │
                                            ▼
                                   List<LayoutProfile>
                                            │
                                            ▼
                              Plugin's variant reader
                              (one instance per profile)
```

**Adding a new variant** (JP, OC, ...) is a JSON file in `data/layouts/`
plus a one-line registration. Never a code change to this project. See
[`data/layouts/README.md`](../data/layouts/README.md) for the format.

## Tests

`tests/Mahjong.Plugin.Game.Tests/` — `Result<T, E>` round-trip, JSON loader
hex-string + decimal handling, real shipping `emj.json` parse sanity check,
`ActionStateMachine` transitions.
