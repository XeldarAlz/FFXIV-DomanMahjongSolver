using DomanMahjongAI.Engine;
using System;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Simulator;

/// <summary>
/// Plays a single mahjong hand forward using the given policies. MVP: discard-only
/// (no calls, no riichi, no ron). Wins detected by tsumo (shanten -1 with yaku).
/// Wall exhaustion → ryuukyoku.
/// </summary>
public sealed class HandSimulator
{
    private readonly Random rng;
    public int MaxTurns { get; set; } = 200;

    public enum Outcome { Tsumo, Ron, Ryuukyoku, Aborted }

    public record HandResult(
        Outcome Outcome,
        int WinnerSeat,           // -1 if no winner
        int LoserSeat,            // ron target, -1 otherwise
        int[] FinalScores,
        int TurnCount,
        int TotalDiscards,
        int[] RiichiDeclared);    // 0/1 per seat

    public HandSimulator(Random rng)
    {
        this.rng = rng;
    }

    public HandResult Simulate(
        IPolicy[] policies,
        int dealer = 0,
        int[]? startingScores = null,
        int round = 0,
        int honba = 0)
    {
        if (policies.Length != 4) throw new ArgumentException("need 4 policies");

        var state = new SimulationHand
        {
            Dealer = dealer,
            Round = round,
            Honba = honba,
            CurrentSeat = dealer,
        };
        if (startingScores is not null)
            for (int i = 0; i < 4; i++) state.Scores[i] = startingScores[i];
        else
            for (int i = 0; i < 4; i++) state.Scores[i] = 25000;

        DealInitialHands(state);

        int turnCount = 0;
        int totalDiscards = 0;

        while (state.Wall.Count > 0 && turnCount < MaxTurns)
        {
            // Current seat draws (dealer starts with 14 on turn 0, everyone else draws then).
            int seat = state.CurrentSeat;
            int handSize = state.HandTileCount(seat);
            if (handSize == 13)
            {
                if (state.Wall.Count == 0) break;
                var drawn = state.Wall.Dequeue();
                state.ClosedCounts[seat][drawn.Id]++;
                state.LastDrawnTile = drawn;
                handSize = 14;

                // Tsumo check.
                if (IsTsumoWin(state, seat))
                {
                    ApplyTsumoScore(state, seat);
                    return new HandResult(
                        Outcome.Tsumo, seat, -1,
                        (int[])state.Scores.Clone(), turnCount, totalDiscards,
                        ToIntArray(state.Riichi));
                }
            }

            // Build snapshot and ask policy.
            var snap = state.ToSnapshot(seat, ActionFlags.Discard);
            var choice = policies[seat].Choose(snap);

            if (choice.Kind == ActionKind.Tsumo)
            {
                if (IsTsumoWin(state, seat))
                {
                    ApplyTsumoScore(state, seat);
                    return new HandResult(
                        Outcome.Tsumo, seat, -1,
                        (int[])state.Scores.Clone(), turnCount, totalDiscards,
                        ToIntArray(state.Riichi));
                }
                // Policy wanted tsumo but hand isn't actually a win — treat as abort.
                return new HandResult(
                    Outcome.Aborted, -1, -1,
                    (int[])state.Scores.Clone(), turnCount, totalDiscards,
                    ToIntArray(state.Riichi));
            }

            // Declare riichi: commit 1000 points to stick pool, mark seat.
            if (choice.Kind == ActionKind.Riichi && !state.Riichi[seat] && state.Scores[seat] >= 1000)
            {
                state.Riichi[seat] = true;
                state.Scores[seat] -= 1000;
                state.RiichiSticks++;
            }

            // Default: discard. Coerce non-discard choices to a safe default.
            Tile discardTile;
            if ((choice.Kind == ActionKind.Discard || choice.Kind == ActionKind.Riichi)
                && choice.DiscardTile is { } d
                && state.ClosedCounts[seat][d.Id] > 0)
            {
                discardTile = d;
            }
            else
            {
                discardTile = PickFallbackDiscard(state, seat);
            }

            state.ClosedCounts[seat][discardTile.Id]--;
            state.Discards[seat].Add(discardTile);
            bool tedashi = state.LastDrawnTile is null || state.LastDrawnTile.Value.Id != discardTile.Id;
            state.DiscardIsTedashi[seat].Add(tedashi);
            state.LastDrawnTile = null;
            totalDiscards++;

            // Ron check: any of the other three seats holds a winning hand with this tile?
            for (int offset = 1; offset <= 3; offset++)
            {
                int other = (seat + offset) % 4;
                if (IsRonWin(state, other, discardTile))
                {
                    ApplyRonScore(state, winner: other, loser: seat, wintile: discardTile);
                    return new HandResult(
                        Outcome.Ron, other, seat,
                        (int[])state.Scores.Clone(), turnCount, totalDiscards,
                        ToIntArray(state.Riichi));
                }
            }

            // Advance seat.
            state.CurrentSeat = (state.CurrentSeat + 1) % 4;
            turnCount++;
        }

        return new HandResult(
            Outcome.Ryuukyoku, -1, -1,
            (int[])state.Scores.Clone(), turnCount, totalDiscards,
            ToIntArray(state.Riichi));
    }

    private static int[] ToIntArray(bool[] b)
    {
        var result = new int[b.Length];
        for (int i = 0; i < b.Length; i++) result[i] = b[i] ? 1 : 0;
        return result;
    }

    private void DealInitialHands(SimulationHand state)
    {
        // Build a 136-tile wall: 4 copies of each 34-space tile.
        var wall = new List<Tile>(136);
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < Tile.CopiesPerKind; c++)
                wall.Add(Tile.FromId(k));

        // Shuffle.
        for (int i = wall.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (wall[i], wall[j]) = (wall[j], wall[i]);
        }

        // Deal 13 tiles per seat (52 total). Dealer gets dealt first.
        for (int seat = 0; seat < 4; seat++)
        {
            for (int i = 0; i < 13; i++)
            {
                var t = wall[^1];
                wall.RemoveAt(wall.Count - 1);
                state.ClosedCounts[seat][t.Id]++;
            }
        }

        // Dead wall: 14 tiles. Take from the bottom.
        var dead = new List<Tile>();
        for (int i = 0; i < 14 && wall.Count > 0; i++)
        {
            dead.Add(wall[0]);
            wall.RemoveAt(0);
        }
        state.DoraIndicator = dead.Count > 0 ? dead[0] : Tile.FromId(0);

        // Remaining = live wall (70 tiles).
        foreach (var t in wall) state.Wall.Enqueue(t);
    }

    private bool IsRonWin(SimulationHand state, int seat, Tile discardedTile)
    {
        // Ron win: adding the discarded tile to seat's 13-tile hand creates a winning shape
        // with at least one yaku. Furiten not modeled here.
        int handSize = state.HandTileCount(seat);
        if (handSize != 13) return false;

        state.ClosedCounts[seat][discardedTile.Id]++;

        var tiles = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < state.ClosedCounts[seat][k]; c++)
                tiles.Add(Tile.FromId(k));
        var hand = Hand.FromTiles(tiles, state.Melds[seat].ToArray());
        var ctx = new WinContext(
            discardedTile,
            WinKind.Ron,
            IsRiichi: state.Riichi[seat],
            RoundWindTileId: 27 + state.Round,
            SeatWindTileId: 27 + seat,
            IsDealer: seat == state.Dealer);

        var result = ScoreEvaluator.Evaluate(hand, ctx);

        state.ClosedCounts[seat][discardedTile.Id]--;
        return result is not null;
    }

    private void ApplyRonScore(SimulationHand state, int winner, int loser, Tile wintile)
    {
        state.ClosedCounts[winner][wintile.Id]++;

        var tiles = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < state.ClosedCounts[winner][k]; c++)
                tiles.Add(Tile.FromId(k));
        var hand = Hand.FromTiles(tiles, state.Melds[winner].ToArray());
        var ctx = new WinContext(
            wintile,
            WinKind.Ron,
            IsRiichi: state.Riichi[winner],
            RoundWindTileId: 27 + state.Round,
            SeatWindTileId: 27 + winner,
            IsDealer: winner == state.Dealer);

        var result = ScoreEvaluator.Evaluate(hand, ctx);
        state.ClosedCounts[winner][wintile.Id]--;

        if (result is null) return;

        // Ron payment: full total paid by the ron target.
        int total = result.Payments.RonTotal;
        state.Scores[loser] -= total;
        state.Scores[winner] += total;

        // Winner collects riichi sticks on the table.
        state.Scores[winner] += state.RiichiSticks * 1000;
        state.RiichiSticks = 0;
    }

    private bool IsTsumoWin(SimulationHand state, int seat)
    {
        // Requires 14-tile hand with shanten -1 and at least one yaku.
        int total = state.HandTileCount(seat);
        if (total != 14) return false;

        // Reconstruct hand as tile list for the evaluator.
        var tiles = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < state.ClosedCounts[seat][k]; c++)
                tiles.Add(Tile.FromId(k));

        var hand = Hand.FromTiles(tiles, state.Melds[seat].ToArray());
        if (state.LastDrawnTile is null) return false;

        var ctx = new WinContext(
            state.LastDrawnTile.Value,
            WinKind.Tsumo,
            IsRiichi: state.Riichi[seat],
            RoundWindTileId: 27 + state.Round,
            SeatWindTileId: 27 + seat,
            IsDealer: seat == state.Dealer);

        var result = ScoreEvaluator.Evaluate(hand, ctx);
        return result is not null;
    }

    private void ApplyTsumoScore(SimulationHand state, int seat)
    {
        var tiles = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
            for (int c = 0; c < state.ClosedCounts[seat][k]; c++)
                tiles.Add(Tile.FromId(k));
        var hand = Hand.FromTiles(tiles, state.Melds[seat].ToArray());
        var ctx = new WinContext(
            state.LastDrawnTile!.Value,
            WinKind.Tsumo,
            IsRiichi: state.Riichi[seat],
            RoundWindTileId: 27 + state.Round,
            SeatWindTileId: 27 + seat,
            IsDealer: seat == state.Dealer);

        var result = ScoreEvaluator.Evaluate(hand, ctx);
        if (result is null) return;

        // Non-dealer winner: dealer pays DealerPay, each non-dealer pays NonDealerPay.
        // Dealer winner: each non-dealer pays NonDealerPay.
        var pay = result.Payments;

        // Apply riichi-stick bonus.
        state.Scores[seat] += state.RiichiSticks * 1000;
        state.RiichiSticks = 0;
        if (seat == state.Dealer)
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == seat) continue;
                state.Scores[i] -= pay.NonDealerPay;
                state.Scores[seat] += pay.NonDealerPay;
            }
        }
        else
        {
            for (int i = 0; i < 4; i++)
            {
                if (i == seat) continue;
                int owed = (i == state.Dealer) ? pay.DealerPay : pay.NonDealerPay;
                state.Scores[i] -= owed;
                state.Scores[seat] += owed;
            }
        }
    }

    private Tile PickFallbackDiscard(SimulationHand state, int seat)
    {
        for (int k = 0; k < Tile.Count34; k++)
            if (state.ClosedCounts[seat][k] > 0) return Tile.FromId(k);
        return Tile.FromId(0);
    }
}
