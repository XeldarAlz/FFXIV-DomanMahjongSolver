namespace DomanMahjongAI.Engine;

/// <summary>
/// Fu accounting for a decomposed winning hand. Chiitoitsu is flat 25.
/// Pinfu tsumo is flat 20; pinfu ron is flat 30.
/// All other hands accumulate fu and round up to the nearest 10.
/// </summary>
public static class FuCalculator
{
    public static int Compute(Decomposition d, WinContext ctx, bool isPinfu)
    {
        if (d.Form == DecompositionForm.Chiitoitsu) return 25;
        if (d.Form == DecompositionForm.Kokushi) return 30; // irrelevant for yakuman score

        if (isPinfu)
            return ctx.IsTsumo ? 20 : 30;

        int fu = 20;
        if (ctx.IsTsumo) fu += 2;
        if (d.IsMenzen && ctx.IsRon) fu += 10;  // menzen kafu

        foreach (var g in d.Groups)
            fu += GroupFu(g, ctx, d.WinningTileFromOpponent);

        fu += WaitFu(d, ctx);

        // Round up to nearest 10.
        int rem = fu % 10;
        if (rem != 0) fu += 10 - rem;
        return fu;
    }

    private static int GroupFu(Group g, WinContext ctx, bool winFromOpponent)
    {
        switch (g.Kind)
        {
            case GroupKind.Run:
                return 0;

            case GroupKind.Pair:
                int pairFu = 0;
                if (g.First.IsDragon) pairFu += 2;
                if (g.First.IsWind)
                {
                    if (g.First.Id == ctx.RoundWindTileId) pairFu += 2;
                    if (g.First.Id == ctx.SeatWindTileId) pairFu += 2;
                }
                return pairFu;

            case GroupKind.Triplet:
            {
                bool effectiveOpen = g.IsOpen ||
                    (g.IsCompletedByWinningTile && winFromOpponent);
                bool th = g.First.IsTerminalOrHonor;
                return effectiveOpen ? (th ? 4 : 2) : (th ? 8 : 4);
            }

            case GroupKind.Kan:
            {
                bool effectiveOpen = g.IsOpen;   // kans don't shanpon-ron
                bool th = g.First.IsTerminalOrHonor;
                return effectiveOpen ? (th ? 16 : 8) : (th ? 32 : 16);
            }

            default:
                return 0;
        }
    }

    private static int WaitFu(Decomposition d, WinContext ctx)
    {
        var completing = d.Groups.FirstOrDefault(g => g.IsCompletedByWinningTile);
        if (completing.Kind == default && !d.Groups.Any(g => g.IsCompletedByWinningTile))
            return 0;

        var winId = ctx.WinningTile.Id;

        switch (completing.Kind)
        {
            case GroupKind.Pair:
                return 2; // tanki

            case GroupKind.Run:
            {
                int first = completing.First.Id;
                int firstMod = first % 9;
                if (winId == first + 1) return 2;                // kanchan
                if (winId == first + 2 && firstMod == 0) return 2; // penchan 1-2 waiting 3
                if (winId == first && firstMod == 6) return 2;     // penchan 8-9 waiting 7
                return 0;                                         // ryanmen
            }

            default:
                return 0; // shanpon: triplet fu already handled (as effective open for ron)
        }
    }
}
