namespace DomanMahjongAI.Engine;

/// <summary>
/// Top-level scorer: enumerate decompositions, detect yaku for each, compute fu+score,
/// and return the highest-scoring valid interpretation. Returns null if no yaku.
/// </summary>
public static class ScoreEvaluator
{
    public static ScoreResult? Evaluate(Hand hand, WinContext ctx)
    {
        var decomps = HandDecomposer.Enumerate(hand, ctx);
        if (decomps.Count == 0) return null;

        ScoreResult? best = null;
        int bestTotal = -1;

        foreach (var d in decomps)
        {
            var yaku = YakuDetector.Detect(d, ctx);
            if (yaku.Count == 0) continue;

            int dora = CountDora(d, ctx);
            bool isYakuman = yaku.Any(y => y.IsYakuman);
            int han = yaku.Sum(y => y.Han);
            if (!isYakuman) han += dora;

            bool isPinfu = yaku.Any(y => y.Yaku == Yaku.Pinfu);
            int fu = FuCalculator.Compute(d, ctx, isPinfu);

            var (basePoints, tier) = ScoreCalculator.BasePoints(han, fu, isYakuman);
            var payments = ScoreCalculator.Pay(basePoints, ctx.IsDealer, ctx.Kind);

            if (payments.Total > bestTotal)
            {
                bestTotal = payments.Total;
                best = new ScoreResult(d, yaku, han, fu, basePoints, payments, tier);
            }
        }

        return best;
    }

    private static int CountDora(Decomposition d, WinContext ctx)
    {
        int count = 0;
        // Gather all tiles in the hand.
        var counts = new int[Tile.Count34];
        foreach (var g in d.Groups)
            foreach (var t in g.Tiles) counts[t.Id]++;

        foreach (var ind in ctx.Dora)
            count += counts[DoraNext(ind)];

        if ((ctx.IsRiichi || ctx.IsDoubleRiichi) && d.IsMenzen)
            foreach (var ind in ctx.UraDora)
                count += counts[DoraNext(ind)];

        return count;
    }

    private static int DoraNext(Tile indicator)
    {
        int id = indicator.Id;
        if (id < 27)
        {
            int suit = id / 9;
            int num = id % 9;
            return suit * 9 + (num + 1) % 9;
        }
        if (id <= 30)     // winds E→S→W→N→E
            return 27 + (id - 27 + 1) % 4;
        // dragons: haku → hatsu → chun → haku
        return 31 + (id - 31 + 1) % 3;
    }
}
