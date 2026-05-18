using Mahjong.Rules;

namespace Mahjong.Engine;

/// <summary>
/// Top-level scoring orchestrator. Replaces the ScoreEvaluator + YakuDetector +
/// FuCalculator + ScoreCalculator quadruple from before Phase 2.
///
/// Pipeline per hand:
///   1. Enumerate all valid decompositions (HandDecomposer).
///   2. For each decomposition:
///      a. Run every IYakuRule, collect all hits.
///      b. If any yakuman fires, drop non-yakuman hits.
///      c. Apply declarative conflicts (e.g. Ryanpeikou removes Iipeiko).
///      d. Add dora to han for non-yakuman hands.
///      e. Reject if total han is below the rule set's MinHan.
///      f. Compute fu (IFuRule), tier (IScoringRule), payments.
///   3. Return the highest-paying valid decomposition.
///
/// The Scorer is stateless beyond the injected IRuleSet, so it's safe to share
/// across threads as long as the rule set is immutable (which all built-in
/// rule sets are).
/// </summary>
public sealed class Scorer
{
    private readonly IRuleSet rules;

    public Scorer(IRuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = rules;
    }

    public ScoreResult? Evaluate(Hand hand, WinContext ctx)
    {
        var decompositions = HandDecomposer.Enumerate(hand, ctx);
        if (decompositions.Count == 0)
            return null;

        ScoreResult? best = null;
        int bestTotal = -1;
        foreach (var d in decompositions)
        {
            var candidate = ScoreOne(d, ctx);
            if (candidate is null)
                continue;
            if (candidate.Payments.Total > bestTotal)
            {
                bestTotal = candidate.Payments.Total;
                best = candidate;
            }
        }
        return best;
    }

    private ScoreResult? ScoreOne(Decomposition d, WinContext ctx)
    {
        var hits = DetectYakuInternal(d, ctx);
        if (hits.Count == 0)
            return null;

        bool isYakuman = AnyYakuman(hits);
        int han = TotalHan(hits);
        if (!isYakuman)
        {
            han += CountDora(d, ctx);
            if (han < rules.MinHan)
                return null;
        }

        int fu = rules.FuRule.Compute(d, ctx, hits);
        var tier = rules.ScoringRule.ResolveTier(han, fu, isYakuman);
        var payments = rules.ScoringRule.Pay(tier, ctx.IsDealer, ctx.Kind);

        return new ScoreResult(d, hits, han, fu, tier.BasePoints, payments, tier.Name);
    }

    /// <summary>
    /// Run the rule set's yaku detection pipeline (collect + yakuman shortcircuit
    /// + conflict resolution) without doing the rest of the scoring pipeline.
    /// Useful for tests and tooling that want to assert on yaku presence.
    /// </summary>
    public IReadOnlyList<YakuHit> DetectYaku(Decomposition d, WinContext ctx)
        => DetectYakuInternal(d, ctx);

    private List<YakuHit> DetectYakuInternal(Decomposition d, WinContext ctx)
    {
        var hits = new List<YakuHit>(8);
        foreach (var rule in rules.YakuRules)
            hits.AddRange(rule.Detect(d, ctx));

        if (AnyYakuman(hits))
            return KeepOnlyYakuman(hits);

        ApplyConflicts(hits);
        return hits;
    }

    private void ApplyConflicts(List<YakuHit> hits)
    {
        if (hits.Count == 0)
            return;

        var hitYaku = new HashSet<Mahjong.Core.Yaku>();
        foreach (var h in hits)
            hitYaku.Add(h.Yaku);

        var toRemove = new HashSet<Mahjong.Core.Yaku>();
        foreach (var rule in rules.YakuRules)
        {
            if (rule.Conflicts.Count == 0)
                continue;
            if (!hitYaku.Contains(rule.Definition.Id))
                continue;
            foreach (var conflict in rule.Conflicts)
                toRemove.Add(conflict);
        }

        if (toRemove.Count > 0)
            hits.RemoveAll(h => toRemove.Contains(h.Yaku));
    }

    private int CountDora(Decomposition d, WinContext ctx)
    {
        int count = 0;
        var counts = new int[Tile.Count34];
        foreach (var g in d.Groups)
            foreach (var t in g.Tiles)
                counts[t.Id]++;

        foreach (var indicator in ctx.Dora)
            count += counts[rules.DoraRule.Next(indicator).Id];

        bool uraEligible = (ctx.IsRiichi || ctx.IsDoubleRiichi) && d.IsMenzen;
        if (uraEligible)
        {
            foreach (var indicator in ctx.UraDora)
                count += counts[rules.DoraRule.Next(indicator).Id];
        }

        // Akadora: red 5m/5p/5s each count as +1 han when present in the hand.
        // Sourced from WinContext.AkaDora because Tile carries only the
        // 34-tile id with no IsRed bit. The variant reader counts reds during
        // hand-decode (raw indices 34/35/36 past the texture base) and ships
        // the total in the snapshot. Yakuman is already gated above, so
        // akadora cannot inflate a yakuman win.
        count += ctx.AkaDora;

        return count;
    }

    private static bool AnyYakuman(List<YakuHit> hits)
    {
        foreach (var h in hits)
        {
            if (h.IsYakuman)
                return true;
        }
        return false;
    }

    private static int TotalHan(List<YakuHit> hits)
    {
        int total = 0;
        foreach (var h in hits)
            total += h.Han;
        return total;
    }

    private static List<YakuHit> KeepOnlyYakuman(List<YakuHit> hits)
    {
        var only = new List<YakuHit>(hits.Count);
        foreach (var h in hits)
        {
            if (h.IsYakuman)
                only.Add(h);
        }
        return only;
    }
}
