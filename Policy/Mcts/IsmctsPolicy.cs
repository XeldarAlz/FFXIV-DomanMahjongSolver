using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Opponents;
using System;

namespace DomanMahjongAI.Policy.Mcts;

/// <summary>
/// Information-Set MCTS policy (plan §8). Falls through to <see cref="EfficiencyPolicy"/>
/// for non-close decisions and non-discard actions. On close discard decisions
/// (top-2 heuristic gap &lt; ε), runs <see cref="MctsSearch"/> for a bounded budget
/// and picks the action with highest mean rollout value.
/// </summary>
public sealed class IsmctsPolicy : IPolicy
{
    private readonly EfficiencyPolicy fastPolicy;
    private readonly OpponentModel opponentModel;
    private readonly MctsSearch search;
    private readonly double closeDecisionEpsilon;

    public IsmctsPolicy(
        EfficiencyPolicy? fastPolicy = null,
        OpponentModel? opponentModel = null,
        int determinizations = 8,
        int simsPerDeterminization = 50,
        int topK = 4,
        int rolloutDepth = 3,
        double closeDecisionEpsilon = 5.0,
        int? rngSeed = null)
    {
        this.fastPolicy = fastPolicy ?? new EfficiencyPolicy();
        this.opponentModel = opponentModel ?? new OpponentModel();
        var rng = rngSeed is null ? new Random() : new Random(rngSeed.Value);
        this.search = new MctsSearch(
            new Determinizer(rngSeed),
            new Rollout(rng, rolloutDepth),
            determinizations,
            simsPerDeterminization,
            topK);
        this.closeDecisionEpsilon = closeDecisionEpsilon;
    }

    public ActionChoice Choose(StateSnapshot state)
    {
        var fast = fastPolicy.Choose(state);

        if (fast.Kind != ActionKind.Discard) return fast;
        if (!IsCloseDecision(state)) return fast;

        opponentModel.Update(state);
        var results = search.Run(state, opponentModel);
        if (results.Length == 0) return fast;

        var best = results[0];
        return ActionChoice.Discard(
            best.Discard,
            $"mcts pick={best.Discard} mean={best.MeanValue:F1} visits={best.Visits}");
    }

    private bool IsCloseDecision(StateSnapshot state)
    {
        if (!state.Legal.Can(ActionFlags.Discard)) return false;

        opponentModel.Update(state);
        var scored = DiscardScorer.Score(state, opponentModel: opponentModel);
        if (scored.Length < 2) return false;

        double gap = scored[0].Score - scored[1].Score;
        return gap < closeDecisionEpsilon;
    }
}
