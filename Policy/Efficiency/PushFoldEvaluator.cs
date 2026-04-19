using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Opponents;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Decide push vs fold on the current turn (plan §7.4).
///   EV_push = P(win) × winValue − Σ_opp P(deal-in|opp) × dealInCost(opp) − P(noten) × notenPenalty
///   fold ⇔ EV_push &lt; 0 under placement-adjusted value
///
/// When folding, the discard scorer should switch to pure-safe mode — cut tiles from
/// highest-danger to lowest, subject to "never break tenpai unless deal-in EV dominates."
///
/// Phase 2 MVP: we don't yet have full P(win) or winValue estimation, so we use a
/// pragmatic rule:
/// <list type="bullet">
///   <item>If shanten ≥ 2 and wall remaining &lt; 15, prefer fold (no realistic win path).</item>
///   <item>If any opponent has declared riichi, lower the push threshold (defend harder).</item>
///   <item>Never fold from tenpai unless the expected deal-in cost exceeds tenpai's minimum agari value (~1000).</item>
/// </list>
/// </summary>
public static class PushFoldEvaluator
{
    public record struct Decision(bool Fold, string Reason);

    public static Decision Evaluate(
        StateSnapshot state,
        int currentShanten,
        OpponentModel opponentModel,
        Tile candidateDiscard)
    {
        bool anyRiichi = false;
        int maxTenpaiProb10 = 0;
        for (int opp = 0; opp < OpponentModel.OpponentCount; opp++)
        {
            int absSeat = (state.OurSeat + 1 + opp) % 4;
            if (state.Seats[absSeat].Riichi) anyRiichi = true;
            int tp10 = (int)(opponentModel.TenpaiProb[opp] * 10);
            if (tp10 > maxTenpaiProb10) maxTenpaiProb10 = tp10;
        }

        double dealInCost = opponentModel.ExpectedDealInCost(candidateDiscard.Id);

        // Tenpai: only fold if deal-in cost clearly exceeds the baseline agari.
        if (currentShanten == 0)
        {
            if (dealInCost > 3000)
                return new Decision(true, $"tenpai but expected deal-in cost {dealInCost:F0} > 3000");
            return new Decision(false, $"tenpai, push (deal-in cost {dealInCost:F0})");
        }

        // 1-shanten with wall remaining: still realistic. Push unless a riichi-ed opponent
        // makes this tile dangerous.
        if (currentShanten == 1)
        {
            if (anyRiichi && dealInCost > 500)
                return new Decision(true, $"1-shanten vs riichi, danger {dealInCost:F0}");
            return new Decision(false, "1-shanten, push");
        }

        // ≥ 2-shanten: fold when either the round is late or an opponent threatens.
        if (state.WallRemaining < 15)
            return new Decision(true, $"{currentShanten}-shanten with wall {state.WallRemaining} remaining");
        if (anyRiichi)
            return new Decision(true, $"{currentShanten}-shanten vs a riichi");
        if (maxTenpaiProb10 >= 7)
            return new Decision(true, $"{currentShanten}-shanten, an opponent tenpai-prob ≥ 0.7");

        return new Decision(false, $"{currentShanten}-shanten, opponents quiet, push");
    }
}
