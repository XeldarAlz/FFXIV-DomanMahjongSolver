# Ruleset specification

**Status:** Phase 0 deliverable (audit). Reconciliation happens in Phase 2 (`Mahjong.Rules`).
**Last updated:** 2026-05-07.

This document is the source of truth for *what mahjong rules the codebase implements*
and *what we still don't know about Doman Mahjong (FFXIV's variant)*. It exists so that
Phase 2 (`Mahjong.Rules` + `IRuleSet`) can be designed against a concrete spec instead of
guesses.

The spec is layered:

1. **Riichi rules** — what's encoded in `Engine/` today, verified by ~140 tests.
2. **Doman rules** — what FFXIV's Doman Mahjong does. Some of this is known from the
   client; some is a TODO list to verify in-game.
3. **Two `IRuleSet` implementations** — `RiichiRuleSet` (used by `Mahjong.Replay` for
   Tenhou logs) and `DomanRuleSet` (used by the live plugin). They share machinery
   (`Mahjong.Engine`) but plug in different rule-tables.

---

## 1. What `Engine/` currently implements (= the `RiichiRuleSet` starter)

The current engine encodes **stock Japanese riichi rules** as found in most reference
implementations. There is no Doman-specific divergence in the code today — the audit
confirmed this against `Engine/YakuDetector.cs` and `Engine/ScoreCalculator.cs`.

### 1.1 Yaku list (39 total)

Defined in `Engine/Yaku.cs` and detected by `Engine/YakuDetector.cs`.

| Han | Yaku | Concealed only? | Notes |
|-----|------|-----------------|-------|
| **1 han** | Riichi | yes | Requires `WinContext.IsRiichi && IsMenzen`. |
| 1 han | Ippatsu | yes | Within one go-around of riichi declaration. |
| 1 han | MenzenTsumo | yes | Self-draw winning, closed hand. |
| 1 han | Pinfu | yes | All sequences, valueless pair, two-sided wait, no fu beyond base. |
| 1 han | Tanyao | no | All simples (2-8). Open tanyao (`kuitan`) **is allowed** — this is a riichi-rules choice. |
| 1 han | Iipeiko | yes | Two identical sequences in same suit. |
| 1 han | Yakuhai (Haku/Hatsu/Chun) | no | Triplet of dragon. |
| 1 han | Yakuhai (Round/Seat wind) | no | Triplet of round wind or seat wind. |
| 1 han | Rinshan | no | Win on replacement tile after kan. |
| 1 han | Chankan | no | Robbing a kan upgrade. |
| 1 han | Haitei | no | Win on last wall draw. |
| 1 han | Houtei | no | Win on last discard. |
| **2 han** | DoubleRiichi | yes | Riichi declared on first uninterrupted turn. |
| 2 han | Chiitoitsu | yes | Seven pairs. |
| 2 han (1 open) | SanshokuDoujun | no | Same sequence in three suits. |
| 2 han | SanshokuDoukou | no | Same triplet in three suits. |
| 2 han (1 open) | Ittsu | no | 123-456-789 in one suit. |
| 2 han | Toitoi | no | All triplets. |
| 2 han | Sanankou | no | Three concealed triplets (counts ankan). |
| 2 han | Sankantsu | no | Three kans. |
| 2 han | Honroutou | no | All terminals + honors. |
| 2 han | Shousangen | no | Two dragon triplets + dragon pair. |
| 2 han (1 open) | Chanta | no | Every group contains a terminal or honor. |
| **3 han** | Ryanpeikou | yes | Two iipeiko (concealed only). Supersedes iipeiko. |
| 3 han (2 open) | Junchan | no | Every group contains a terminal (no honors). Supersedes chanta. |
| 3 han (2 open) | Honitsu | no | One suit + honors. |
| **6 han** | Chinitsu (5 open) | no | One suit only. |

### 1.2 Yakuman list (12)

| Yakuman | Notes |
|---------|-------|
| Kokushi Musou | Thirteen orphans. |
| Suuankou | Four concealed triplets. *Tanki variant* (single-wait → double yakuman) is **not currently distinguished**. |
| Daisangen | All three dragon triplets. |
| Shousuushii | Three wind triplets + wind pair. |
| Daisuushii | All four wind triplets. |
| Tsuuiisou | All honors. |
| Chinroutou | All terminals (no simples, no honors). |
| Ryuuiisou | All green tiles (2,3,4,6,8 sou + hatsu). |
| Chuuren Poutou | Nine gates. *Pure variant* (9-sided wait → double yakuman) is **not currently distinguished**. |
| Suukantsu | Four kans. |
| Tenhou | Dealer wins on initial draw. |
| Chihou | Non-dealer wins on first draw before any call. |

**Counted yakuman (kazoe):** 13+ han from non-yakuman yaku is treated as a single yakuman
in `ScoreCalculator.BasePoints`.

### 1.3 Conflict / supersession rules

Currently encoded imperatively in `YakuDetector.Detect`:

- Ryanpeikou removes Iipeiko.
- Junchan removes Chanta.
- Yakuman shortcircuits — if any yakuman fires, normal yaku are suppressed.

Phase 2 makes these declarative on each `IYakuRule.Conflicts`.

### 1.4 Fu calculation

Defined in `Engine/FuCalculator.cs`:

- Chiitoitsu: flat 25 fu, no other components added.
- Standard form: 20 base.
  - +10 menzen kafu (closed-hand ron).
  - +2 tsumo (except pinfu tsumo, which is flat 20).
  - +0 / +2 / +4 / +8 / +16 / +32 per group based on (open vs closed) × (sequence /
    triplet / kan) × (terminal/honor vs simple).
  - +2 yakuhai pair (round wind / seat wind / dragon).
  - +2 single-tile wait (kanchan / penchan / tanki).
- Rounds up to nearest 10.

### 1.5 Scoring tiers (`ScoreCalculator.BasePoints`)

| Han | Base points | Tier |
|-----|-------------|------|
| < 5 | `fu × 2^(han+2)` capped at 2000 | (raw / mangan) |
| 5 | 2000 | mangan |
| 6-7 | 3000 | haneman |
| 8-10 | 4000 | baiman |
| 11-12 | 6000 | sanbaiman |
| 13+ | 8000 | yakuman (counted) |
| Yakuman | 8000 × multiplier | yakuman |

### 1.6 Payment formula (`ScoreCalculator.Pay`)

- Dealer ron: base × 6, rounded up to 100.
- Dealer tsumo: each non-dealer pays base × 2, rounded up to 100.
- Non-dealer ron: base × 4, rounded up to 100.
- Non-dealer tsumo: dealer pays base × 2, each other non-dealer pays base × 1.

### 1.7 Dora

- `Engine/ScoreEvaluator.cs:DoraNext` cycles winds 27→28→29→30→27 and dragons
  31→32→33→31. Suits cycle 1→2→...→9→1 within suit.
- Red dora (akadora) is **not modeled** — there is no concept of `Tile.IsRed`.
- Ura-dora is treated identically to dora when riichi is declared and the indicator
  is supplied.

### 1.8 What's deliberately not encoded (out of scope today)

- **Renhou** — non-dealer wins on first turn before drawing. Some rule variants treat
  it as mangan/yakuman; we treat it as nothing.
- **Daichisei** — seven pairs of honors only. Treated as Chiitoitsu + Tsuuiisou today.
- **Open-hand single-wait fu bonus distinctions for kokushi/chiihou variants.**
- **Pao (sekinin barai)** — the responsibility-payment rule when a player feeds a
  third dragon/wind triplet that completes daisangen/daisuushii.

---

## 2. Doman Mahjong (FFXIV variant) — known and unknown

Source material: FFXIV in-game tooltips, Square Enix's Lodestone documentation, and
empirical observation of the `Emj`/`EmjL` addons via `tools/scan_tiles.py`. None of
this is currently codified in tests.

### 2.1 Known from the client

These are confirmed by the addon dump or in-game UI text:

- **Tile set:** standard 34-tile + 4 copies = 136 tiles. No flowers, no jokers.
- **Player count:** 4 players, East-South-West-North seating.
- **Hand size:** 13 tiles + 1 drawn = 14 tiles to declare.
- **Yaku names rendered in the UI** (subset, partial): Riichi, Ippatsu, Tsumo, Pinfu,
  Tanyao, Yakuhai, Toitoi, Honitsu, Chinitsu, Kokushi, Suuankou, Daisangen,
  Shousuushii, Daisuushii, Tsuuiisou, Chinroutou, Ryuuiisou, ChuurenPoutou, Suukantsu,
  Tenhou, Chihou. Doman uses the same Japanese names.
- **Two-han minimum:** Doman scoring tooltips reference a minimum of **2 han to win**.
  This differs from standard riichi (1 han minimum). **TODO verify.**
- **Round structure:** East round only by default in casual play; East+South in
  ranked. **TODO confirm whether East-only is the only mode the plugin needs to
  support.**

### 2.2 Open questions — to verify in Phase 2

Each of these resolves a specific `IRuleSet` configuration knob.

| # | Question | Why it matters | How to verify |
|---|----------|----------------|---------------|
| Q1 | Is the minimum-han threshold 1 or 2? | Affects `IScoringRule.MinHan`; affects whether yaku-less tsumo is a no-yaku abort. | Score a hand worth exactly 1 han (e.g. tanyao only) at a Doman table; check if the win is allowed. |
| Q2 | Does open tanyao (`kuitan`) count? | One-line difference in `TanyaoRule`. | Open a tanyao hand by calling chi/pon and confirm the yaku still appears in the win screen. |
| Q3 | Is double riichi recognized? | Toggles `DoubleRiichiRule`. | Declare riichi on the first uninterrupted turn; check yaku list. |
| Q4 | Is ippatsu recognized? | Toggles `IppatsuRule`. | Already known yes from UI strings, but confirm interaction with calls (call should void ippatsu). |
| Q5 | Are red dora (akadora) in play? | Adds `Tile.IsRed` to `Mahjong.Core` + a `RedDoraRule`. | Inspect the wall during a hand for visually-different 5m/5p/5s tiles. |
| Q6 | Are kuitan / kuipinfu allowed? | Affects which yaku survive opening the hand. | Score open hands and check which yaku are still credited. |
| Q7 | Can suuankou tanki be a double yakuman? | `SuuankouTankiRule` as a separate detector. | Win a suuankou hand on a single-tile wait. |
| Q8 | Is chuuren poutou pure-9-wait a double yakuman? | `ChuurenPureWaitRule`. | Same — win one with the 9-wait. |
| Q9 | Pao / sekinin barai? | Adds `IResponsibilityRule`. | Feed a third dragon to an opponent and observe the payment split. |
| Q10 | What's the kazoe yakuman threshold? | Riichi: 13+ han = single yakuman. Doman: ? | Build a 13-han hand and observe the score. |
| Q11 | Is renhou recognized? Mangan, yakuman, or nothing? | Adds or omits `RenhouRule`. | Win on first turn as non-dealer before any call. |
| Q12 | Starting points / oka / uma? | Affects `IPlacementPolicy` calibration, not the engine directly. | Read in-game settings; record the score-table layout. |
| Q13 | Honba and riichi-stick handling? | Affects `IScoringRule.Pay` final payment. | Win after multiple honba and observe the bonus. |

**Until Q1-Q11 are answered**, `DomanRuleSet` ships in Phase 2 as a copy of `RiichiRuleSet`
with **2-han minimum** as the only divergence (Q1 is the highest-confidence Doman delta).
Each subsequent answer is one rule swap, not a re-architecture.

### 2.3 Doman rules `Mahjong.Replay` must NOT use

`Mahjong.Replay` parses Tenhou logs. Tenhou is **standard riichi**, not Doman. Replays
must construct `RiichiRuleSet`, never `DomanRuleSet`. Mixing rulesets corrupts tuning
data — for example, scoring a 1-han Tenhou hand under Doman's 2-han minimum would
silently zero its EV.

This is a load-bearing reason for `IRuleSet` to be injected at the call site, not
configured globally.

---

## 3. Phase 2 contract

Phase 2 (`Mahjong.Rules`) delivers:

```csharp
namespace Mahjong.Rules;

public interface IRuleSet
{
    string Name { get; }                                  // "Doman" / "Riichi"
    IReadOnlyList<IYakuRule> YakuRules { get; }
    IScoringRule              ScoringRule { get; }
    IDoraRule                 DoraRule { get; }
    FuConfiguration           FuConfig { get; }
    bool   AllowsRedDora { get; }
    bool   AllowsKuitan  { get; }
    int    MinHan        { get; }                          // 1 for Riichi, likely 2 for Doman (Q1)
    int    KazoeThreshold { get; }                         // 13 for Riichi
    int    MaxYakuman    { get; }                          // 2 for Riichi (e.g. suuankou-tanki)
}

public interface IYakuRule
{
    YakuDefinition           Definition { get; }
    YakuHit?                 Detect(Decomposition d, WinContext ctx);
    IReadOnlyList<Yaku>      Conflicts  { get; }            // declarative supersession
}

public sealed record YakuDefinition(
    Yaku    Id,
    string  Name,
    int     ClosedHan,
    int     OpenHan,                                        // 0 means "open form not allowed"
    bool    IsYakuman,
    bool    RequiresMenzen);

public interface IScoringRule { Payments Pay(int han, int fu, bool isDealer, WinKind kind, IRuleSet rules); }
public interface IDoraRule    { Tile     Next(Tile indicator); int CountReds(IReadOnlyList<Tile> tiles); }
```

**Implementations in Phase 2:**
- `RiichiRuleSet` — drop-in replacement for current `Engine/YakuDetector.cs` behavior.
  Tenhou parser uses this. All 140 existing Engine tests must stay green when run
  through `RiichiRuleSet`.
- `DomanRuleSet` — initially identical to `RiichiRuleSet` except `MinHan = 2`. As Q2-Q13
  resolve, individual rules swap.
- One `IYakuRule` implementation per yaku — ~28 normal + 12 yakuman = 40 rule files.
  Each file is < 50 lines, testable in isolation.

**What gets deleted:** `Engine/YakuDetector.cs` (400-line static dispatcher),
`Engine/FuCalculator.cs`, `Engine/ScoreCalculator.cs`, `Engine/ScoreEvaluator.cs`.
Their behavior is split between `Mahjong.Rules` (rule definitions) and
`Mahjong.Engine.Scorer` (orchestration).

---

## 4. Verification plan

Phase 2 is "done" when:

1. All 140 Engine tests pass when wired to `RiichiRuleSet`.
2. A new `Mahjong.Rules.Tests` suite has a parity test for every yaku in section 1.1
   (closed and open variants where applicable).
3. A "Doman delta" test fixture exists as a stub — empty initially, filled in as
   Q2-Q13 resolve.
4. `Mahjong.Replay` integration tests prove Tenhou logs score identically when run
   through the old `Engine.YakuDetector` and the new `RiichiRuleSet` — bit-exact on
   han/fu/payment for every hand in the regression corpus.
