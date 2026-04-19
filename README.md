# FFXIV-MahjongAI

Dalamud plugin that plays FFXIV Doman Mahjong. Heuristic + ISMCTS policy, rule-based Bayesian opponent model, fully client-side C# — no Python, no GPU, no training pipeline. Target skill: upper-intermediate to low-expert (Tenhou 4–6 dan equivalent).

## Current status

Actively-developed. Discard-only auto-play works; more features landing incrementally.

| Area | State |
|---|---|
| Engine (tiles, shanten, ukeire, yaku, fu, score) | complete, 129 tests |
| EfficiencyPolicy (discard scorer + riichi eval + placement adjuster + call eval + push/fold) | 37 tests |
| OpponentModel (tenpai prob + hand marginal + danger map) | scaffolded; needs opponent-discard data |
| ISMCTS policy | scaffolded; tree search in progress |
| Dispatch: discard, pass, call | live (via `AtkUnitBase.FireCallback`) |
| Dispatch: riichi, kan, tsumo, ron | not yet mapped |
| AddonEmj RE | hand + scores read; discard pools + dealer + round still owed |
| Tests total | 166 passing, 0 warnings |

## Install (custom Dalamud repo)

Because this plugin contains automation, it cannot ship via the official Dalamud repo. Install via the custom-repo flow:

1. In-game: `/xlsettings` → **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste:
   ```
   https://raw.githubusercontent.com/XeldarAlz/FFXIV-MahjongAI/main/repo/repo.json
   ```
3. Enable the checkbox. Click **Save and Close**.
4. Open `/xlplugins`, search **Doman Mahjong AI**, **Install**.
5. Run `/mjauto debug` in chat to open the overlay. Accept the ToS modal to arm automation.

Plugin is off-by-default. Automation requires explicit acknowledgement of the in-plugin ToS disclosure.

## Usage

- `/mjauto debug` — open the debug overlay (main UI).
- `/mjauto on` / `off` — arm/disarm automation.
- `/mjauto policy eff` — select efficiency policy (ismcts coming later).

Overlay panels:
- **Auto-play** — live state + last action taken.
- **AddonEmj status** — whether the in-match addon is detected.
- **Suggestions** — top-5 discard picks with reasoning (shanten, ukeire, dora, yakuhai, expected deal-in cost).
- **Actions** — manual Auto-discard / Test discard slot N / Test pass / diagnostic memory dumps.

## Architecture

```
┌──── Dalamud Plugin (net10-windows) ──────────────────────────────┐
│   AddonEmjReader → StateSnapshot → Policy → InputDispatcher       │
│   AutoPlayLoop watches state, dispatches with humanized delay     │
└───────────────────────────────────────────────────────────────────┘
        │                                         │
        ▼                                         ▼
  Engine (net8.0) ────────────┐       Policy (net8.0)
  Tiles, shanten, ukeire,      │       EfficiencyPolicy, DiscardScorer,
  yaku, fu, score, wall,       │       RiichiEvaluator, CallEvaluator,
  decomposer, StateSnapshot    │       PushFoldEvaluator, PlacementAdjuster
                               │       OpponentModel, IsmctsPolicy, Determinizer
                               │
                               └── All pure C#, no Dalamud deps, unit-testable
```

## Safety

- **ToS acceptance gate** blocks automation until explicitly acknowledged.
- **Suggestion-only mode** shows policy picks without dispatching inputs.
- **Dispatch guards** — every FireCallback attempt returns `HookFailed` silently on invalid state (no crashes).
- **Humanized timing** — log-normal ~900ms median, 400ms floor, 2500ms cap.
- **Kill switch** — unchecking "Automation armed" halts the loop mid-hand.

## Development

### Build

```
dotnet build DomanMahjongAI.sln
```

### Tests

```
dotnet test DomanMahjongAI.sln
```

### Project layout

```
FFXIV-MahjongAI/
├── DomanMahjongAI/       Dalamud plugin entry + UI + dispatch + reader
├── Engine/               Core mahjong primitives (no Dalamud deps)
├── Policy/               Decision logic (no Dalamud deps)
├── tests/
│   ├── Engine.Tests/
│   └── Policy.Tests/
├── repo/
│   └── repo.json         Custom Dalamud repo manifest
└── .github/workflows/    CI + release workflows
```

### Maintaining a release

1. Bump `AssemblyVersion` in `DomanMahjongAI/DomanMahjongAI.csproj` AND `repo/repo.json`.
2. Push tag `vX.Y.Z`. `.github/workflows/release.yml` auto-builds + uploads `latest.zip` to a new GitHub release. `repo.json` already points at `releases/latest/download/latest.zip`, so no further edit needed per release.

## Plan

Long-form implementation plan at [`doman-mahjong-ai-plan-heuristic.md`](../doman-mahjong-ai-plan-heuristic.md) (local file, not in repo).

Short version of remaining work:

1. **Phase 1 finish** — capture dispatch patterns for Riichi / Kan / Tsumo / Ron (needs live play).
2. **Phase 2** — RE opponent discard pools, dealer, round; use them in `OpponentModel` for real danger maps.
3. **Phase 3** — Tenhou-log replay harness + weight tuner.
4. **Phase 4** — ISMCTS tree search (decision / chance nodes, UCB1, rollouts).
5. **Phase 5** — polish overlay, add stats panel, ship real release.

## License

AGPL-3.0-or-later.

## Acknowledgements

- Structural exemplar: [ffxiv-rota](https://github.com/XeldarAlz/ffxiv-rota) (my own Dalamud plugin).
- Plan and heuristic design drawn from public Tenhou / Riichi literature and the Suphx/Mortal papers — but deliberately avoids ML-track dependencies.
