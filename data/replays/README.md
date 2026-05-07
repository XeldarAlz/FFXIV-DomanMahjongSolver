# Tenhou replay fixtures

Test corpus for the policy regression suite. Each `*.tenhou.json` file is a
single-kyoku Tenhou-format log; the matching `*.snapshot.json` is the golden
file the regression harness pins behavior against.

## Format

Tenhou's [tenhou.net/6](https://tenhou.net/6) JSON shape — `Mahjong.Replay.TenhouLog`
parses these directly. Synthetic fixtures here are minimal — enough to drive the
parser and replay seat 0 — but valid against the same parser as production logs.

## Adding real Tenhou logs

1. Download a `tenhou.net/6/log` URL, save as `<descriptive-name>.tenhou.json`.
2. Drop it in this directory.
3. Run the test suite once with `UPDATE_REPLAY_SNAPSHOTS=1` set — the harness
   will produce the matching `<descriptive-name>.snapshot.json`.
4. Inspect the snapshot. If the policy's choices look reasonable, commit both
   the `.tenhou.json` and the `.snapshot.json` side-by-side.

Subsequent runs compare against the snapshot. A behavior change in the policy
shows up as a failing regression test — review the diff, decide if the new
behavior is the new baseline, and regenerate the snapshot if so.

## Why riichi rules, not Doman rules

Tenhou games are played under standard Japanese riichi rules. Replaying them
under `DomanRuleSet` would silently corrupt accuracy — for instance, if Doman
enforces a 2-han minimum (`docs/ruleset.md` Q1), Tenhou-recorded 1-han wins
would be rejected as no-yaku, and tuner deltas would chase noise.

The harness wires `RiichiRuleSet` automatically. Do not pass a Doman policy
into the replay test loop.
