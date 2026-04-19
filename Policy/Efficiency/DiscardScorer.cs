using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Scores each legal discard from a 14-tile hand.
///
/// Without an opponent model (M7+), the scorer weights:
///   w_shanten   : hard penalty per point of shanten (reject shanten regressions unless no other option)
///   w_ukeire_k  : reward per distinct accepted kind
///   w_ukeire_w  : reward per live tile accepted (weighted by wall visibility)
///   w_dora      : reward per dora tile retained
///   w_yakuhai   : reward for retaining a dragon pair or our seat/round wind pair
///   w_terminal  : small preference for discarding isolated terminal/honor tiles first
///
/// Weights are hand-tuned defaults for M5; the M9 weight-tuner will replace them
/// with replay-corpus-optimized values.
/// </summary>
public static class DiscardScorer
{
    public record struct Weights(
        double Shanten,
        double UkeireKinds,
        double UkeireWeighted,
        double Dora,
        double Yakuhai,
        double IsolatedTerminal,
        double DealInCost)
    {
        public static Weights Default => new(
            Shanten: 100.0,           // dominates — shanten regressions basically rejected
            UkeireKinds: 2.0,
            UkeireWeighted: 1.0,
            Dora: 4.0,
            Yakuhai: 2.0,
            IsolatedTerminal: 0.5,
            DealInCost: 0.001);       // points × probability → small scaling factor
    }

    public readonly record struct ScoredDiscard(
        Tile Discard,
        double Score,
        int ShantenAfter,
        int UkeireKinds,
        int UkeireWeighted,
        int DoraRetained,
        int YakuhaiRetained,
        double DealInCost);

    public static ScoredDiscard[] Score(
        StateSnapshot state,
        Weights? weights = null,
        Wall? wall = null,
        Placement.PlacementAdjuster.Weights? placement = null,
        Opponents.OpponentModel? opponentModel = null)
    {
        var w = weights ?? Weights.Default;
        var p = placement ?? Placement.PlacementAdjuster.ComputeFor(state);

        var hand = BuildHand(state);
        if (hand.ClosedTileCount + hand.OpenMelds.Count * 3 != 14)
            throw new ArgumentException(
                $"DiscardScorer requires a 14-tile hand (closed={hand.ClosedTileCount}, melds={hand.OpenMelds.Count})");

        var ukeire = UkeireEnumerator.Enumerate(hand, wall);
        var result = new ScoredDiscard[ukeire.Length];

        for (int i = 0; i < ukeire.Length; i++)
        {
            var u = ukeire[i];
            int doraRetained = CountDora(hand, u.Discard, state.DoraIndicators);
            int yakuhaiRetained = CountYakuhai(hand, u.Discard, 27 + state.RoundWind, state);

            double dealInCost = opponentModel?.ExpectedDealInCost(u.Discard.Id) ?? 0.0;

            double score =
                -w.Shanten * Math.Max(0, u.ShantenAfter)
                + w.UkeireKinds * u.AcceptedKinds.Length * p.UkeireMultiplier
                + w.UkeireWeighted * u.WeightedCount * p.UkeireMultiplier
                + w.Dora * doraRetained * p.HandValueMultiplier
                + w.Yakuhai * yakuhaiRetained * p.HandValueMultiplier
                + (IsIsolatedTerminalOrHonor(hand, u.Discard) ? w.IsolatedTerminal * p.DangerMultiplier : 0.0)
                - w.DealInCost * dealInCost * p.DangerMultiplier;

            result[i] = new ScoredDiscard(
                u.Discard, score, u.ShantenAfter,
                u.AcceptedKinds.Length, u.WeightedCount,
                doraRetained, yakuhaiRetained, dealInCost);
        }

        Array.Sort(result, (a, b) => b.Score.CompareTo(a.Score));
        return result;
    }

    private static Hand BuildHand(StateSnapshot state)
    {
        var counts = new int[Tile.Count34];
        foreach (var t in state.Hand) counts[t.Id]++;
        return new Hand(counts, state.OurMelds);
    }

    private static int CountDora(Hand hand, Tile removed, IReadOnlyList<Tile> indicators)
    {
        if (indicators.Count == 0) return 0;
        int total = 0;
        for (int id = 0; id < Tile.Count34; id++)
        {
            int count = hand.ClosedCounts[id] - (removed.Id == id ? 1 : 0);
            if (count <= 0) continue;
            foreach (var ind in indicators)
                if (DoraNext(ind) == id) total += count;
        }
        // Tiles inside open melds also count as dora; open melds don't change on discard.
        foreach (var m in hand.OpenMelds)
            foreach (var t in m.Tiles)
                foreach (var ind in indicators)
                    if (DoraNext(ind) == t.Id) total++;
        return total;
    }

    private static int DoraNext(Tile indicator)
    {
        int id = indicator.Id;
        if (id < 27) return (id / 9) * 9 + (id % 9 + 1) % 9;
        if (id <= 30) return 27 + (id - 27 + 1) % 4;
        return 31 + (id - 31 + 1) % 3;
    }

    private static int CountYakuhai(Hand hand, Tile removed, int roundWindTileId, StateSnapshot state)
    {
        // Dragons always count. Winds count iff they match round wind or the seat's own wind.
        int seatWindTileId = 27 + state.OurSeat;   // seat 0 = E(27), 1 = S(28), 2 = W(29), 3 = N(30)
        int total = 0;
        for (int id = 27; id < Tile.Count34; id++)
        {
            int count = hand.ClosedCounts[id] - (removed.Id == id ? 1 : 0);
            if (count <= 0) continue;
            bool isYakuhai =
                id >= 31                        // dragons
                || id == roundWindTileId        // round wind
                || id == seatWindTileId;        // seat wind
            if (isYakuhai) total += count;
        }
        return total;
    }

    /// <summary>
    /// An isolated terminal/honor = tile has no neighbors in hand (or pair) that'd make it useful.
    /// Cheap heuristic: a terminal/honor tile with no duplicate and no adjacent same-suit tiles.
    /// </summary>
    private static bool IsIsolatedTerminalOrHonor(Hand hand, Tile t)
    {
        if (!t.IsTerminalOrHonor) return false;
        if (hand.ClosedCounts[t.Id] >= 2) return false;   // pair candidate

        if (t.IsHonor) return true;   // honors have no neighbors

        // Terminal suited: check one-step neighbor in suit.
        int suitBase = (t.Id / 9) * 9;
        int pos = t.Id - suitBase;
        int neighborPos = pos == 0 ? 1 : 7;   // 1 → 2; 9 → 8
        int neighborId = suitBase + neighborPos;
        if (hand.ClosedCounts[neighborId] > 0) return false;

        // And two-step for kanchan possibility.
        int twoStepPos = pos == 0 ? 2 : 6;
        int twoStepId = suitBase + twoStepPos;
        if (hand.ClosedCounts[twoStepId] > 0) return false;

        return true;
    }
}
