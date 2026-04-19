using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Placement;

/// <summary>
/// Placement-aware weight multipliers (plan §9). Mahjong ratings reward final rank,
/// not raw score — a bot that maximises EV(score) loses to one that maximises EV(rank).
/// Behaviour shifts most aggressively on the last hand of the match, but is active
/// throughout the hanchan.
///
/// Returns multipliers for the discard-scorer terms:
///   <c>DangerWeight</c>     — higher = fold harder (avoid deal-ins)
///   <c>UkeireWeight</c>     — lower during fold mode, higher during push mode
///   <c>HandValueWeight</c>  — encourage big hands when we need big hands (4th seat, last-round)
/// </summary>
public static class PlacementAdjuster
{
    public record struct Weights(
        double DangerMultiplier,
        double UkeireMultiplier,
        double HandValueMultiplier)
    {
        public static Weights Neutral => new(1.0, 1.0, 1.0);
    }

    /// <summary>
    /// Compute weight multipliers from the current score state.
    /// Simplified: rank 1 → fold mode; rank 4 → push mode; middle → neutral.
    /// The plan's fuller table keys on (rank, score gap, rounds remaining, dealer position)
    /// — that richer version lives in the M9 weight-tuner output. For now we use ranks + round end.
    /// </summary>
    public static Weights ComputeFor(StateSnapshot state)
    {
        var rank = RankOf(state, state.OurSeat);
        bool lastHand = IsLastHand(state);
        int scoreGapAbove = ScoreGapToHigherRank(state, rank);
        int scoreGapBelow = ScoreGapToLowerRank(state, rank);

        return rank switch
        {
            1 => lastHand && scoreGapBelow > 8000
                    ? new Weights(DangerMultiplier: 2.0, UkeireMultiplier: 0.5, HandValueMultiplier: 0.5)
                    : new Weights(DangerMultiplier: 1.3, UkeireMultiplier: 0.9, HandValueMultiplier: 0.9),

            2 or 3 => lastHand
                    ? new Weights(DangerMultiplier: 1.1, UkeireMultiplier: 1.0, HandValueMultiplier: 1.2)
                    : Weights.Neutral,

            4 => lastHand
                    ? new Weights(DangerMultiplier: 0.4, UkeireMultiplier: 1.3, HandValueMultiplier: 1.8)
                    : new Weights(DangerMultiplier: 0.7, UkeireMultiplier: 1.1, HandValueMultiplier: 1.3),

            _ => Weights.Neutral,
        };
    }

    /// <summary>1-indexed rank (1 = highest score) of the given seat in this snapshot.</summary>
    public static int RankOf(StateSnapshot state, int seat)
    {
        var ourScore = state.Scores[seat];
        int rank = 1;
        for (int i = 0; i < state.Scores.Count; i++)
            if (i != seat && state.Scores[i] > ourScore) rank++;
        return rank;
    }

    private static int ScoreGapToHigherRank(StateSnapshot state, int ourRank)
    {
        if (ourRank <= 1) return int.MaxValue;
        int ourScore = state.Scores[state.OurSeat];
        int minGap = int.MaxValue;
        foreach (var s in state.Scores)
            if (s > ourScore && s - ourScore < minGap) minGap = s - ourScore;
        return minGap;
    }

    private static int ScoreGapToLowerRank(StateSnapshot state, int ourRank)
    {
        if (ourRank >= 4) return int.MaxValue;
        int ourScore = state.Scores[state.OurSeat];
        int minGap = int.MaxValue;
        foreach (var s in state.Scores)
            if (s < ourScore && ourScore - s < minGap) minGap = ourScore - s;
        return minGap;
    }

    /// <summary>
    /// Heuristic: last hand of the hanchan. We don't have a reliable "final hand" field yet
    /// (would need M4 round + honba extraction); approximate by wall-remaining ≤ 10 combined
    /// with round wind = South. When round-tracking lands, tighten this.
    /// </summary>
    public static bool IsLastHand(StateSnapshot state)
    {
        return state.RoundWind == 1 && state.WallRemaining <= 10;
    }
}
