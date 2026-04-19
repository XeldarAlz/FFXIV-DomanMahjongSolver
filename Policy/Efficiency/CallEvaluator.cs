using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Decide whether to accept a pon/chi/kan call offer. Plan §7.2 rules (simplified for
/// phase 1 — opponent-model-aware deal-in weighting comes in phase 2):
/// <list type="bullet">
///   <item>Yaku path preserved or created: tanyao, yakuhai, toitoi, honitsu, chinitsu</item>
///   <item>Shanten does not regress</item>
///   <item>Expected hand value doesn't drop below a floor</item>
///   <item>Don't sacrifice riichi path unless the new open path dominates in expectation</item>
/// </list>
/// Without the opponent model, we use a conservative policy: call only when the meld
/// clearly helps shanten AND the hand still has a reachable yaku path.
/// </summary>
public static class CallEvaluator
{
    public record struct Decision(bool Accept, MeldCandidate? Chosen, string Reason);

    public static Decision Evaluate(StateSnapshot state)
    {
        var legal = state.Legal;
        var candidates = new System.Collections.Generic.List<MeldCandidate>();
        candidates.AddRange(legal.PonCandidates);
        candidates.AddRange(legal.ChiCandidates);
        candidates.AddRange(legal.KanCandidates);

        if (candidates.Count == 0)
            return new Decision(false, null, "no call candidates offered");

        // Build the closed-hand counts we have RIGHT NOW (pre-call).
        var counts = new int[Tile.Count34];
        foreach (var t in state.Hand) counts[t.Id]++;

        // Current shanten of our 13-tile hand.
        int currentShanten = ComputeShanten(counts, state.OurMelds.Count);

        MeldCandidate? best = null;
        int bestShantenDelta = 0;
        string bestReason = "";

        foreach (var c in candidates)
        {
            // Simulate the call: consume HandTiles from our hand, add a group to melds
            // (for shanten purposes, it counts as a "called set" just like an open meld).
            bool canConsume = c.HandTiles.All(t => counts[t.Id] > 0);
            if (!canConsume) continue;

            foreach (var t in c.HandTiles) counts[t.Id]--;
            int meldsAfter = state.OurMelds.Count + 1;
            int shantenAfter = ComputeShanten(counts, meldsAfter);
            foreach (var t in c.HandTiles) counts[t.Id]++;

            // Accept iff shanten strictly improves (going from 1-shanten to tenpai), AND
            // the hand still has a reachable yaku.
            int delta = currentShanten - shantenAfter;
            if (delta <= 0) continue;

            if (!HasReachableYaku(counts, meldsAfter, state, c))
                continue;

            if (delta > bestShantenDelta)
            {
                bestShantenDelta = delta;
                best = c;
                bestReason = $"shanten {currentShanten}→{shantenAfter}, meld kind={c.Kind}";
            }
        }

        if (best is null)
            return new Decision(false, null, $"no call improves shanten with yaku (current shanten={currentShanten})");

        return new Decision(true, best, bestReason);
    }

    private static int ComputeShanten(int[] counts, int meldCount)
    {
        int std = ShantenCalculator.Standard(counts, meldCount);
        int ci = meldCount == 0 ? ShantenCalculator.Chiitoitsu(counts) : 8;
        int ko = meldCount == 0 ? ShantenCalculator.Kokushi(counts) : 8;
        return System.Math.Min(std, System.Math.Min(ci, ko));
    }

    /// <summary>
    /// Cheap yaku-reachability check: does the simulated post-call hand have at least one
    /// plausible yaku path? Checks: tanyao (no terminals/honors), yakuhai (dragon or our-wind
    /// triplet), toitoi (all triplets possible), honitsu (one suit + honors dominates),
    /// chinitsu (one suit only). Conservative — false negatives lean toward not-calling.
    /// </summary>
    private static bool HasReachableYaku(int[] counts, int meldsAfter,
                                         StateSnapshot state, MeldCandidate thisCall)
    {
        // Yakuhai: any dragon pair/triplet already in hand OR this call is a yakuhai pon.
        if (thisCall.Kind == MeldKind.Pon && IsYakuhai(thisCall.ClaimedTile, state)) return true;
        for (int id = 31; id < Tile.Count34; id++)
            if (counts[id] >= 2) return true;
        for (int id = 27; id <= 30; id++)
            if (counts[id] >= 2 && IsYakuhaiWind(id, state)) return true;

        // Tanyao: no terminals/honors anywhere.
        bool tanyaoPossible = true;
        for (int id = 0; id < Tile.Count34; id++)
        {
            if (counts[id] == 0) continue;
            var t = Tile.FromId(id);
            if (t.IsTerminalOrHonor) { tanyaoPossible = false; break; }
        }
        if (tanyaoPossible)
        {
            foreach (var m in state.OurMelds)
                foreach (var t in m.Tiles)
                    if (t.IsTerminalOrHonor) { tanyaoPossible = false; goto tanyaoDone; }
            tanyaoDone:;
        }
        if (tanyaoPossible && thisCall.ClaimedTile.IsSimple) return true;

        // Honitsu/chinitsu: all tiles in a single suit (+ honors for honitsu).
        int? dominantSuit = null;
        bool hasOffSuit = false;
        for (int id = 0; id < 27; id++)
        {
            if (counts[id] == 0) continue;
            int suit = id / 9;
            if (dominantSuit is null) dominantSuit = suit;
            else if (dominantSuit != suit) { hasOffSuit = true; break; }
        }
        if (!hasOffSuit && dominantSuit is not null) return true;

        // Toitoi if already has ≥ 2 triplets in combined hand+melds.
        int tripletCount = state.OurMelds.Count(m => m.Kind == MeldKind.Pon || m.Kind is MeldKind.MinKan or MeldKind.AnKan or MeldKind.ShouMinKan);
        for (int id = 0; id < Tile.Count34; id++)
            if (counts[id] >= 3) tripletCount++;
        if (tripletCount >= 2 && thisCall.Kind != MeldKind.Chi) return true;

        return false;
    }

    private static bool IsYakuhai(Tile t, StateSnapshot state)
    {
        if (t.IsDragon) return true;
        if (!t.IsWind) return false;
        int seatWindId = 27 + state.OurSeat;
        return t.Id == seatWindId || t.Id == 27 + state.RoundWind;
    }

    private static bool IsYakuhaiWind(int id, StateSnapshot state)
    {
        int seatWindId = 27 + state.OurSeat;
        return id == seatWindId || id == 27 + state.RoundWind;
    }
}
