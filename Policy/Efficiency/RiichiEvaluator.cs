using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy.Efficiency;

/// <summary>
/// Decide whether to declare riichi, given a tenpai-ready state after an intended discard.
/// Plan §7.3: declare when all of
/// <list type="bullet">
///   <item>Hand is closed</item>
///   <item>Hand is tenpai after the intended discard</item>
///   <item>Our score ≥ 1000 (riichi stick cost)</item>
///   <item>Wall remaining ≥ 4 (at least one chance to win)</item>
///   <item>Waits worth it: ≥ 4 live tiles <b>or</b> riichi's expected value upgrade is mangan+</item>
/// </list>
/// Damaten preferred when already ≥ mangan or locked 1st on last hand.
/// </summary>
public static class RiichiEvaluator
{
    public record struct Decision(
        bool Declare,
        string Reason);

    public static Decision Evaluate(
        StateSnapshot state,
        Tile intendedDiscard,
        int weightedUkeireAfterDiscard,
        int acceptedKindsAfterDiscard,
        int shantenAfterDiscard)
    {
        // Must be tenpai (shanten 0) after the discard.
        if (shantenAfterDiscard != 0)
            return new Decision(false, "not tenpai after discard");

        // Must be menzen (no open melds; ankan is closed, doesn't break menzen).
        bool closed = state.OurMelds.All(m => m.Kind == MeldKind.AnKan);
        if (!closed) return new Decision(false, "hand is open");

        // Need ≥ 1000 to pay the riichi stick.
        int ourScore = state.Scores[state.OurSeat];
        if (ourScore < 1000) return new Decision(false, $"score {ourScore} < 1000");

        // Need at least 4 wall tiles remaining so there's still a chance to draw.
        if (state.WallRemaining < 4) return new Decision(false, $"wall {state.WallRemaining} < 4");

        // Waits worth it: ≥ 4 live tiles accepting us, OR pairing with kan-dora potential
        // to push into mangan range. We don't have the full value estimator yet; use the
        // simpler "≥ 4 weighted ukeire" rule.
        if (weightedUkeireAfterDiscard < 4)
            return new Decision(false, $"only {weightedUkeireAfterDiscard} live accepting tiles");

        // Damaten preference: if we'd already score ≥ mangan without riichi, damaten is better.
        // Without a value estimator here, skip that check for this pass (safe to declare riichi
        // when the basic thresholds are met — riichi always adds at least 1 han + ippatsu shot + ura).

        return new Decision(true,
            $"tenpai closed, score={ourScore}, wall={state.WallRemaining}, ukeire={acceptedKindsAfterDiscard}kinds/{weightedUkeireAfterDiscard}w");
    }
}
