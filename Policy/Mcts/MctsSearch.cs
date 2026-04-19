using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Opponents;
using System;
using System.Collections.Generic;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Information-Set MCTS kernel. For each determinization (sampled hidden info):
/// build a tree rooted at our current state, select via UCB1 with heuristic prior,
/// expand into top-K candidate discards, rollout with fast heuristic, backprop.
/// Repeats for <see cref="simsPerDeterminization"/> iterations.
/// Averages action values across all determinizations and returns the best.
///
/// Deviations from plan §8 for MVP:
/// <list type="bullet">
///   <item>No chance nodes — draws are abstracted into rollout</item>
///   <item>Opponents don't act during rollout — opponent-response modeling is M7 work</item>
///   <item>Progressive widening = simple top-K from heuristic scorer</item>
/// </list>
/// These simplifications keep latency manageable (&lt; 100ms on modern hardware)
/// while still exercising the MCTS framework.
/// </summary>
public sealed class MctsSearch
{
    private readonly Determinizer determinizer;
    private readonly Rollout rollout;
    private readonly int determinizations;
    private readonly int simsPerDeterminization;
    private readonly int topK;
    private readonly double ucbExplorationConstant;
    private readonly double progressiveWideningC;

    public MctsSearch(
        Determinizer determinizer,
        Rollout rollout,
        int determinizations = 8,
        int simsPerDeterminization = 50,
        int topK = 4,
        double ucbExplorationConstant = 1.4,
        double progressiveWideningC = 1.0)
    {
        this.determinizer = determinizer;
        this.rollout = rollout;
        this.determinizations = determinizations;
        this.simsPerDeterminization = simsPerDeterminization;
        this.topK = topK;
        this.ucbExplorationConstant = ucbExplorationConstant;
        this.progressiveWideningC = progressiveWideningC;
    }

    public readonly record struct ActionResult(Tile Discard, double MeanValue, int Visits);

    /// <summary>
    /// Run MCTS and return top candidates by mean value. First entry is the pick.
    /// </summary>
    public ActionResult[] Run(StateSnapshot root, OpponentModel model)
    {
        // Identify candidate discards via the fast scorer — keep the full ranking
        // for progressive widening; children are added as visit count grows.
        var scored = DiscardScorer.Score(root, opponentModel: model);
        if (scored.Length == 0) return [];

        int maxK = Math.Min(topK, scored.Length);
        var allCandidates = new Tile[scored.Length];
        for (int i = 0; i < scored.Length; i++) allCandidates[i] = scored[i].Discard;

        var totalValue = new double[scored.Length];
        var totalVisits = new int[scored.Length];

        for (int det = 0; det < determinizations; det++)
        {
            var sample = determinizer.Sample(root, model);
            if (sample is null) continue;

            var rootNode = new MctsNode(root, null, null);

            // Seed with 1 child; progressive widening adds more as visits accrue.
            AddChild(rootNode, allCandidates[0]);

            for (int sim = 0; sim < simsPerDeterminization; sim++)
            {
                // Progressive widening: expected #children = min(maxK, ceil(c * sqrt(N+1))).
                int desiredChildren = Math.Min(
                    maxK,
                    Math.Max(1, (int)Math.Ceiling(progressiveWideningC * Math.Sqrt(rootNode.Visits + 1))));
                while (rootNode.Children.Count < desiredChildren &&
                       rootNode.Children.Count < allCandidates.Length)
                {
                    AddChild(rootNode, allCandidates[rootNode.Children.Count]);
                }

                var path = new List<MctsNode> { rootNode };
                var current = rootNode;

                while (current.Expanded && current.Children.Count > 0)
                {
                    current = SelectUcb1(current);
                    path.Add(current);
                }

                double value = rollout.Run(current.State, model);

                foreach (var n in path)
                {
                    n.Visits++;
                    n.TotalValue += value;
                }
            }

            // Merge level-1 stats into per-candidate totals.
            for (int i = 0; i < rootNode.Children.Count; i++)
            {
                totalValue[i] += rootNode.Children[i].TotalValue;
                totalVisits[i] += rootNode.Children[i].Visits;
            }
        }

        var results = new ActionResult[allCandidates.Length];
        for (int i = 0; i < allCandidates.Length; i++)
        {
            double mean = totalVisits[i] > 0 ? totalValue[i] / totalVisits[i] : double.NegativeInfinity;
            results[i] = new ActionResult(allCandidates[i], mean, totalVisits[i]);
        }
        Array.Sort(results, (a, b) => b.MeanValue.CompareTo(a.MeanValue));
        return results;
    }

    private static void AddChild(MctsNode parent, Tile discard)
    {
        var childState = ApplyDiscard(parent.State, discard);
        parent.Children.Add(new MctsNode(childState, parent, discard));
        parent.Expanded = true;
    }

    private MctsNode SelectUcb1(MctsNode parent)
    {
        MctsNode best = parent.Children[0];
        double bestScore = best.Ucb1(parent.Visits + 1, ucbExplorationConstant);
        for (int i = 1; i < parent.Children.Count; i++)
        {
            double s = parent.Children[i].Ucb1(parent.Visits + 1, ucbExplorationConstant);
            if (s > bestScore) { best = parent.Children[i]; bestScore = s; }
        }
        return best;
    }

    private static StateSnapshot ApplyDiscard(StateSnapshot state, Tile discarded)
    {
        var newHand = new Tile[state.Hand.Count - 1];
        int w = 0;
        bool removed = false;
        foreach (var t in state.Hand)
        {
            if (!removed && t.Id == discarded.Id) { removed = true; continue; }
            newHand[w++] = t;
        }
        return state with { Hand = newHand };
    }
}
