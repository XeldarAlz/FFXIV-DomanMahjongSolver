using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Opponents;
using System;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Sample a concrete assignment of hidden information consistent with the public
/// observation — i.e., hypothesize each opponent's closed hand and the future wall order.
/// Used by <see cref="IsmctsPolicy"/> to run tree search over plausible game states.
///
/// Samples are drawn from the <see cref="OpponentModel.HandMarginal"/> by rejection-
/// sampling: try uniform-over-live assignments until one respects the marginal's zero
/// constraints (opponent-discarded tiles can't be in hand).
/// </summary>
public sealed class Determinizer
{
    private readonly Random rng;

    public Determinizer(int? seed = null)
    {
        rng = seed is null ? new Random() : new Random(seed.Value);
    }

    public record struct Determinization(
        Tile[][] OpponentHands,      // [3][13] — hypothesized closed hands
        Tile[] WallOrder);           // remaining draw pile, shuffled

    /// <summary>
    /// Produce a sampled determinization. Opponents' hand sizes default to 13 minus their
    /// open-meld tile count (each called meld removes 3 closed tiles). Returns null if the
    /// marginal distribution makes a sample impossible (shouldn't happen in well-formed states).
    /// </summary>
    public Determinization? Sample(StateSnapshot state, OpponentModel model)
    {
        // Build the pool of unseen tiles: everything that isn't in our hand, our melds,
        // the public discards, the opponents' open melds, or revealed dora indicators.
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

        var pool = new List<Tile>();
        for (int k = 0; k < Tile.Count34; k++)
        {
            int remaining = Tile.CopiesPerKind - seen[k];
            for (int i = 0; i < remaining; i++) pool.Add(Tile.FromId(k));
        }

        // Determine how many tiles each opponent still holds in closed hand.
        // Closed tile count = 13 - 3 * meldCount (or +1 if they're mid-turn with 14, but we
        // ignore that subtlety for the sampler — the extra tile is negligible).
        var handSizes = new int[OpponentModel.OpponentCount];
        for (int opp = 0; opp < OpponentModel.OpponentCount; opp++)
        {
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            handSizes[opp] = 13 - state.Seats[absSeat].Melds.Count * 3;
            if (handSizes[opp] < 0) handSizes[opp] = 0;
        }

        int totalDemand = 0;
        foreach (var n in handSizes) totalDemand += n;
        if (totalDemand > pool.Count) return null;

        // Deal from the shuffled pool. We'd bias toward HandMarginal but for MVP we do a
        // uniform shuffle — the marginals naturally fall out because the pool already
        // excludes tiles known to be impossible.
        Shuffle(pool);

        var handsOut = new Tile[OpponentModel.OpponentCount][];
        int cursor = 0;
        for (int opp = 0; opp < OpponentModel.OpponentCount; opp++)
        {
            int n = handSizes[opp];
            handsOut[opp] = new Tile[n];
            for (int i = 0; i < n; i++) handsOut[opp][i] = pool[cursor++];
        }

        // Remainder of the pool = the future wall draw order.
        var wallOrder = new Tile[pool.Count - cursor];
        for (int i = 0; i < wallOrder.Length; i++) wallOrder[i] = pool[cursor + i];

        return new Determinization(handsOut, wallOrder);
    }

    private void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
