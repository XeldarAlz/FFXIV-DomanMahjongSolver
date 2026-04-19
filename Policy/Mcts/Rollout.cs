using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Opponents;
using System;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Simplified rollout: from a post-discard 13-tile state, simulate up to
/// <see cref="maxDepth"/> "pseudo turns" where our virtual self keeps drawing and
/// discarding using the fast heuristic.
///
/// With <see cref="simulateOpponents"/>, each step also consumes 3 additional
/// tiles from the unseen pool to represent opponent draws/discards. This shrinks
/// the effective wall and stochastically depletes tiles we might otherwise draw.
///
/// Leaf evaluation: a blend of shanten, weighted ukeire, and dora retained.
/// Higher = better. Positive infinity if the rollout hits agari (shanten -1).
/// </summary>
public sealed class Rollout
{
    private readonly int maxDepth;
    private readonly Random rng;
    private readonly bool simulateOpponents;

    public Rollout(Random rng, int maxDepth = 3, bool simulateOpponents = true)
    {
        this.rng = rng;
        this.maxDepth = maxDepth;
        this.simulateOpponents = simulateOpponents;
    }

    public double Run(StateSnapshot afterDiscard, OpponentModel model)
    {
        var state = afterDiscard;

        // Build initial "seen" counts from the public state — our hand + melds, all
        // seats' discards + melds, and the dora indicator. We'll mutate this as the
        // rollout consumes tiles (us and opponents).
        var seen = BuildSeenCounts(state);

        for (int step = 0; step < maxDepth; step++)
        {
            var drawn = DrawRandomTile(seen);
            if (drawn is null) break;   // wall exhausted
            seen[drawn.Value.Id]++;

            // Add drawn tile to hand → now 14 tiles.
            var handAfterDraw = new Tile[state.Hand.Count + 1];
            for (int i = 0; i < state.Hand.Count; i++) handAfterDraw[i] = state.Hand[i];
            handAfterDraw[^1] = drawn.Value;
            state = state with
            {
                Hand = handAfterDraw,
                Legal = new LegalActions(ActionFlags.Discard, [], [], [], []),
            };

            // Evaluate if this draw is already agari.
            var counts = new int[Tile.Count34];
            foreach (var t in state.Hand) counts[t.Id]++;
            int shanten = ShantenCalculator.Standard(counts, state.OurMelds.Count);
            if (state.OurMelds.Count == 0)
            {
                shanten = System.Math.Min(shanten, ShantenCalculator.Chiitoitsu(counts));
                shanten = System.Math.Min(shanten, ShantenCalculator.Kokushi(counts));
            }
            if (shanten < 0) return double.PositiveInfinity;   // won

            // Fast-policy pick: top discard from the scorer.
            var scored = DiscardScorer.Score(state, opponentModel: model);
            if (scored.Length == 0) break;
            var pick = scored[0].Discard;

            // Remove it — back to 13 tiles.
            var handAfterDiscard = new Tile[state.Hand.Count - 1];
            int w = 0;
            bool removed = false;
            foreach (var t in state.Hand)
            {
                if (!removed && t.Id == pick.Id) { removed = true; continue; }
                handAfterDiscard[w++] = t;
            }
            state = state with { Hand = handAfterDiscard };

            // Opponent simulation: each of 3 opponents draws+discards one tile, all
            // consumed from the same pool. Net effect is 3 extra tiles gone per step.
            if (simulateOpponents)
            {
                for (int opp = 0; opp < 3; opp++)
                {
                    var oppTile = DrawRandomTile(seen);
                    if (oppTile is null) break;
                    seen[oppTile.Value.Id]++;
                }
            }
        }

        return EvaluateLeaf(state, model);
    }

    private static int[] BuildSeenCounts(StateSnapshot state)
    {
        var seen = new int[Tile.Count34];
        foreach (var t in state.Hand) seen[t.Id]++;
        foreach (var m in state.OurMelds)
            foreach (var t in m.Tiles) seen[t.Id]++;
        foreach (var seat in state.Seats)
        {
            foreach (var t in seat.Discards) seen[t.Id]++;
            foreach (var m in seat.Melds)
                foreach (var t in m.Tiles) seen[t.Id]++;
        }
        foreach (var t in state.DoraIndicators) seen[t.Id]++;
        return seen;
    }

    private Tile? DrawRandomTile(int[] seen)
    {
        int total = 0;
        for (int k = 0; k < Tile.Count34; k++) total += System.Math.Max(0, Tile.CopiesPerKind - seen[k]);
        if (total == 0) return null;

        int pick = rng.Next(total);
        for (int k = 0; k < Tile.Count34; k++)
        {
            int live = System.Math.Max(0, Tile.CopiesPerKind - seen[k]);
            if (pick < live) return Tile.FromId(k);
            pick -= live;
        }
        return null;
    }

    private static double EvaluateLeaf(StateSnapshot state, OpponentModel model)
    {
        // For a 13-tile hand (post-discard state), use a blend of shanten + ukeire.
        // For a 14-tile hand (mid-rollout draw), include its shanten too.
        var counts = new int[Tile.Count34];
        foreach (var t in state.Hand) counts[t.Id]++;
        int shanten = ShantenCalculator.Standard(counts, state.OurMelds.Count);
        if (state.OurMelds.Count == 0)
        {
            shanten = System.Math.Min(shanten, ShantenCalculator.Chiitoitsu(counts));
            shanten = System.Math.Min(shanten, ShantenCalculator.Kokushi(counts));
        }

        // Negative shanten is win — shouldn't hit here because Run returns early, but safe.
        if (shanten < 0) return double.PositiveInfinity;

        // Weighted ukeire on current state: how many live tiles accept us?
        int ukeireWeight = 0;
        if (shanten <= 1 && state.Hand.Count >= 13)
        {
            for (int k = 0; k < Tile.Count34; k++)
            {
                if (counts[k] >= Tile.CopiesPerKind) continue;
                counts[k]++;
                int newShanten = ShantenCalculator.Standard(counts, state.OurMelds.Count);
                if (state.OurMelds.Count == 0)
                {
                    newShanten = System.Math.Min(newShanten, ShantenCalculator.Chiitoitsu(counts));
                    newShanten = System.Math.Min(newShanten, ShantenCalculator.Kokushi(counts));
                }
                counts[k]--;

                if (newShanten < shanten) ukeireWeight += Tile.CopiesPerKind - counts[k];
            }
        }

        // Base utility function. Lower shanten + more ukeire = higher value.
        return -100.0 * shanten + 1.0 * ukeireWeight;
    }
}
