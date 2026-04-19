using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Opponents;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Phase 4 MVP: one-ply Monte Carlo evaluation. For each candidate discard among the
/// fast-policy top-K, sample N determinizations of the hidden state, compute a utility,
/// and pick the candidate whose average utility across determinizations is highest.
///
/// This is weaker than the full ISMCTS tree search (plan §8) — no rollouts, no UCB1
/// selection, no progressive widening. It still improves on pure heuristics by
/// incorporating sampled opponent-hand information into the danger estimate: if a
/// sample shows an opponent tenpai waiting on our candidate discard, that candidate
/// gets heavily downweighted.
///
/// Tree search (decision + chance nodes, UCB1, rollout policy) is the next Phase 4 step.
/// </summary>
public sealed class MonteCarloEvaluator
{
    private readonly Determinizer determinizer;
    private readonly int determinizations;
    private readonly int topK;
    private const double KnownWaitDealInCost = 6000.0;   // pessimistic default when a sample catches an opp waiting on our candidate

    public MonteCarloEvaluator(
        Determinizer determinizer,
        int determinizations = 20,
        int topK = 4)
    {
        this.determinizer = determinizer;
        this.determinizations = determinizations;
        this.topK = topK;
    }

    public readonly record struct EvaluatedDiscard(
        Tile Discard,
        double MeanUtility,
        int SampleCount,
        double BaselineUtility);

    /// <summary>
    /// Evaluate the top-K discards under <paramref name="determinizations"/> samples.
    /// Returns the full list sorted by mean utility descending. The caller picks index 0
    /// or re-ranks with additional heuristics (e.g. placement).
    /// </summary>
    public EvaluatedDiscard[] Evaluate(
        StateSnapshot state,
        OpponentModel baselineModel,
        DiscardScorer.Weights? weights = null)
    {
        var w = weights ?? DiscardScorer.Weights.Default;
        var scored = DiscardScorer.Score(state, w, opponentModel: baselineModel);
        int k = System.Math.Min(topK, scored.Length);
        if (k == 0) return [];

        // Build initial totals per candidate.
        var totals = new double[k];
        var counts = new int[k];
        var baseline = new double[k];
        for (int i = 0; i < k; i++) baseline[i] = scored[i].Score;

        for (int det = 0; det < determinizations; det++)
        {
            var sample = determinizer.Sample(state, baselineModel);
            if (sample is null) continue;

            for (int i = 0; i < k; i++)
            {
                double u = UtilityUnderSample(state, scored[i], sample.Value, w);
                totals[i] += u;
                counts[i]++;
            }
        }

        var result = new EvaluatedDiscard[k];
        for (int i = 0; i < k; i++)
        {
            double mean = counts[i] > 0 ? totals[i] / counts[i] : baseline[i];
            result[i] = new EvaluatedDiscard(scored[i].Discard, mean, counts[i], baseline[i]);
        }

        System.Array.Sort(result, (a, b) => b.MeanUtility.CompareTo(a.MeanUtility));
        return result;
    }

    /// <summary>
    /// Utility of a single (candidate discard × sampled opponent hands) pair.
    /// Starts from the candidate's baseline score, then applies a sample-specific
    /// penalty if any opponent's sampled hand is tenpai with our candidate as a wait.
    /// </summary>
    private static double UtilityUnderSample(
        StateSnapshot state,
        DiscardScorer.ScoredDiscard candidate,
        Determinizer.Determinization sample,
        DiscardScorer.Weights weights)
    {
        double utility = candidate.Score;

        // For each sampled opponent, check whether their hypothesized closed hand is
        // tenpai AND whether our candidate discard would complete them.
        for (int opp = 0; opp < sample.OpponentHands.Length; opp++)
        {
            var oppHand = sample.OpponentHands[opp];
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            int meldCount = state.Seats[absSeat].Melds.Count;

            var counts = new int[Tile.Count34];
            foreach (var t in oppHand) counts[t.Id]++;

            int baseShanten = ShantenCalculator.Standard(counts, meldCount);
            if (meldCount == 0)
            {
                baseShanten = System.Math.Min(baseShanten, ShantenCalculator.Chiitoitsu(counts));
                baseShanten = System.Math.Min(baseShanten, ShantenCalculator.Kokushi(counts));
            }
            if (baseShanten > 0) continue;   // sample not tenpai → can't be completed by discard

            // Check if adding our candidate discard to their hand reaches agari (-1 shanten).
            counts[candidate.Discard.Id]++;
            int afterShanten = ShantenCalculator.Standard(counts, meldCount);
            if (meldCount == 0)
            {
                afterShanten = System.Math.Min(afterShanten, ShantenCalculator.Chiitoitsu(counts));
                afterShanten = System.Math.Min(afterShanten, ShantenCalculator.Kokushi(counts));
            }
            counts[candidate.Discard.Id]--;

            if (afterShanten < 0)
            {
                // This discard deals into the sampled hand. Apply penalty proportional to
                // what the deal-in would cost (placement-adjusted already via scorer).
                utility -= weights.DealInCost * KnownWaitDealInCost;
            }
        }

        return utility;
    }
}
