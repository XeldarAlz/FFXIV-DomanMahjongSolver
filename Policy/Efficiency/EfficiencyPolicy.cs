using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Tier-1 heuristic policy (plan §7). Covers the discard decision.
/// Calls (pon/chi/kan), riichi, and push/fold are owed for M8 — for now this
/// passes on call opportunities and declines riichi.
/// </summary>
public sealed class EfficiencyPolicy : IPolicy
{
    private readonly DiscardScorer.Weights weights;

    public EfficiencyPolicy(DiscardScorer.Weights? weights = null)
    {
        this.weights = weights ?? DiscardScorer.Weights.Default;
    }

    public ActionChoice Choose(StateSnapshot state)
    {
        var legal = state.Legal;

        // Agari: if we can win, win. (Yaku check is downstream — the game shouldn't
        // expose Tsumo/Ron in legal actions unless a yaku exists, but trust the
        // caller here.)
        if (legal.Can(ActionFlags.Tsumo))
            return ActionChoice.DeclareTsumo("tsumo legal");
        if (legal.Can(ActionFlags.Ron))
            return ActionChoice.DeclareRon("ron legal");

        // Defer all call decisions (pon/chi/kan) to M8's CallEvaluator.
        if (legal.Can(ActionFlags.Pon) || legal.Can(ActionFlags.Chi) ||
            legal.Can(ActionFlags.MinKan) || legal.Can(ActionFlags.ShouMinKan) ||
            legal.Can(ActionFlags.AnKan))
        {
            if (legal.Can(ActionFlags.Pass))
                return ActionChoice.Pass("call decision owed to M8");
        }

        // Defer riichi decision to M8's RiichiEvaluator; for now just discard normally.
        // (Riichi is technically "discard + declare"; without the evaluator we always
        // choose the plain discard route.)

        if (legal.Can(ActionFlags.Discard))
        {
            var scored = DiscardScorer.Score(state, weights);
            if (scored.Length == 0)
                return ActionChoice.Pass("no legal discards found");

            var best = scored[0];
            var reasoning =
                $"best={best.Discard} shanten={best.ShantenAfter} ukeire={best.UkeireKinds}kinds/{best.UkeireWeighted}w " +
                $"dora={best.DoraRetained} yakuhai={best.YakuhaiRetained} score={best.Score:F1}";
            return ActionChoice.Discard(best.Discard, reasoning);
        }

        return ActionChoice.Pass("no actionable legal action for efficiency policy");
    }
}
