namespace DomanMahjongAI.Engine;

/// <summary>
/// Detects yaku present in a given decomposition under a given WinContext.
/// If any yakuman is found, only yakuman are returned (they don't combine with normal yaku).
/// </summary>
public static class YakuDetector
{
    public static IReadOnlyList<YakuHit> Detect(Decomposition d, WinContext ctx)
    {
        // First check yakuman — if any present, don't bother with normal yaku.
        var yakuman = DetectYakuman(d, ctx);
        if (yakuman.Count > 0) return yakuman;

        var hits = new List<YakuHit>(6);

        // 1 han
        if (ctx.IsRiichi && !ctx.IsDoubleRiichi && d.IsMenzen) hits.Add(new(Yaku.Riichi, 1));
        if (ctx.IsDoubleRiichi && d.IsMenzen) hits.Add(new(Yaku.DoubleRiichi, 2));
        if (ctx.IsIppatsu && d.IsMenzen && (ctx.IsRiichi || ctx.IsDoubleRiichi))
            hits.Add(new(Yaku.Ippatsu, 1));
        if (d.IsMenzen && ctx.IsTsumo) hits.Add(new(Yaku.MenzenTsumo, 1));
        if (ctx.IsRinshan) hits.Add(new(Yaku.Rinshan, 1));
        if (ctx.IsChankan) hits.Add(new(Yaku.Chankan, 1));
        if (ctx.IsHaitei) hits.Add(new(Yaku.Haitei, 1));
        if (ctx.IsHoutei) hits.Add(new(Yaku.Houtei, 1));

        if (CheckPinfu(d, ctx)) hits.Add(new(Yaku.Pinfu, 1));
        if (CheckTanyao(d)) hits.Add(new(Yaku.Tanyao, 1));
        if (CheckIipeiko(d)) hits.Add(new(Yaku.Iipeiko, 1));
        AddYakuhai(d, ctx, hits);

        // 2 han (form-specific)
        if (d.Form == DecompositionForm.Chiitoitsu) hits.Add(new(Yaku.Chiitoitsu, 2));

        if (d.Form == DecompositionForm.Standard)
        {
            if (CheckSanshokuDoujun(d)) hits.Add(new(Yaku.SanshokuDoujun, d.IsMenzen ? 2 : 1));
            if (CheckSanshokuDoukou(d)) hits.Add(new(Yaku.SanshokuDoukou, 2));
            if (CheckIttsu(d)) hits.Add(new(Yaku.Ittsu, d.IsMenzen ? 2 : 1));
            if (CheckToitoi(d)) hits.Add(new(Yaku.Toitoi, 2));
            int ankouCount = d.ConcealedTripletCount +
                             d.Groups.Count(g => g.IsConcealedKan);
            if (ankouCount == 3) hits.Add(new(Yaku.Sanankou, 2));
            if (d.KanCount == 3) hits.Add(new(Yaku.Sankantsu, 2));
            if (CheckShousangen(d)) hits.Add(new(Yaku.Shousangen, 2));
        }

        if (CheckHonroutou(d)) hits.Add(new(Yaku.Honroutou, 2));
        if (d.Form == DecompositionForm.Standard && CheckChantaLike(d, junchan: false))
            hits.Add(new(Yaku.Chanta, d.IsMenzen ? 2 : 1));

        // 3 han
        if (d.Form == DecompositionForm.Standard && CheckRyanpeikou(d) && d.IsMenzen)
        {
            // Ryanpeikou implies iipeiko — remove iipeiko if present.
            hits.RemoveAll(h => h.Yaku == Yaku.Iipeiko);
            hits.Add(new(Yaku.Ryanpeikou, 3));
        }
        if (d.Form == DecompositionForm.Standard && CheckChantaLike(d, junchan: true))
        {
            // Junchan implies chanta — remove chanta.
            hits.RemoveAll(h => h.Yaku == Yaku.Chanta);
            hits.Add(new(Yaku.Junchan, d.IsMenzen ? 3 : 2));
        }
        if (CheckHonitsu(d)) hits.Add(new(Yaku.Honitsu, d.IsMenzen ? 3 : 2));

        // 6 han
        if (CheckChinitsu(d))
        {
            // Chinitsu supersedes honitsu.
            hits.RemoveAll(h => h.Yaku == Yaku.Honitsu);
            hits.Add(new(Yaku.Chinitsu, d.IsMenzen ? 6 : 5));
        }

        return hits;
    }

    // ======================== Yakuman ========================

    private static IReadOnlyList<YakuHit> DetectYakuman(Decomposition d, WinContext ctx)
    {
        var hits = new List<YakuHit>(2);

        if (d.Form == DecompositionForm.Kokushi)
        {
            hits.Add(new(Yaku.Kokushi, 13, IsYakuman: true));
        }

        if (d.Form == DecompositionForm.Standard)
        {
            int ankouCount = d.ConcealedTripletCount + d.Groups.Count(g => g.IsConcealedKan);
            if (ankouCount == 4) hits.Add(new(Yaku.Suuankou, 13, true));

            if (CheckDaisangen(d)) hits.Add(new(Yaku.Daisangen, 13, true));
            if (CheckFourWinds(d, out bool big))
            {
                hits.Add(big
                    ? new(Yaku.Daisuushii, 26, true)    // big four winds: double yakuman
                    : new(Yaku.Shousuushii, 13, true));
            }
            if (CheckTsuuiisou(d)) hits.Add(new(Yaku.Tsuuiisou, 13, true));
            if (CheckChinroutou(d)) hits.Add(new(Yaku.Chinroutou, 13, true));
            if (CheckRyuuiisou(d)) hits.Add(new(Yaku.Ryuuiisou, 13, true));
            if (d.KanCount == 4) hits.Add(new(Yaku.Suukantsu, 13, true));

            if (CheckChuuren(d, ctx, out bool pureChuuren))
            {
                hits.Add(pureChuuren
                    ? new(Yaku.ChuurenPoutou, 26, true)
                    : new(Yaku.ChuurenPoutou, 13, true));
            }
        }

        if (ctx.IsTenhou) hits.Add(new(Yaku.Tenhou, 13, true));
        if (ctx.IsChihou) hits.Add(new(Yaku.Chihou, 13, true));

        return hits;
    }

    // ======================== Individual checks ========================

    private static bool CheckPinfu(Decomposition d, WinContext ctx)
    {
        if (d.Form != DecompositionForm.Standard) return false;
        if (!d.IsMenzen) return false;

        // All non-pair sets must be runs.
        foreach (var g in d.Sets)
            if (g.Kind != GroupKind.Run) return false;

        // Pair must not be yakuhai (dragons, round wind, seat wind).
        var pair = d.Pair.First;
        if (pair.IsDragon) return false;
        if (pair.Id == ctx.RoundWindTileId) return false;
        if (pair.Id == ctx.SeatWindTileId) return false;

        // Wait must be ryanmen (two-sided). Reject kanchan and penchan.
        var winTile = ctx.WinningTile;
        var completingGroup = d.Groups.FirstOrDefault(g => g.IsCompletedByWinningTile);
        if (completingGroup.Kind != GroupKind.Run) return false;

        int offset = winTile.Id - completingGroup.First.Id;  // 0, 1, or 2
        int firstMod = completingGroup.First.Id % 9;

        if (offset == 1) return false;                       // kanchan (middle-of-run wait)
        if (offset == 2 && firstMod == 0) return false;      // penchan 12→3
        if (offset == 0 && firstMod == 6) return false;      // penchan 89→7
        return true;
    }

    private static bool CheckTanyao(Decomposition d)
    {
        if (d.Form == DecompositionForm.Kokushi) return false;
        foreach (var g in d.Groups)
            if (g.ContainsTerminalOrHonor) return false;
        return true;
    }

    private static bool CheckIipeiko(Decomposition d)
    {
        if (!d.IsMenzen || d.Form != DecompositionForm.Standard) return false;
        var runs = d.Groups.Where(g => g.Kind == GroupKind.Run).ToList();
        var seen = new HashSet<int>();
        foreach (var r in runs)
        {
            if (!seen.Add(r.First.Id))
                return true;
        }
        return false;
    }

    private static void AddYakuhai(Decomposition d, WinContext ctx, List<YakuHit> hits)
    {
        foreach (var g in d.Groups)
        {
            if (g.Kind is not (GroupKind.Triplet or GroupKind.Kan)) continue;
            var t = g.First;
            if (t.Id == 31) hits.Add(new(Yaku.YakuhaiHaku, 1));
            else if (t.Id == 32) hits.Add(new(Yaku.YakuhaiHatsu, 1));
            else if (t.Id == 33) hits.Add(new(Yaku.YakuhaiChun, 1));
            if (t.IsWind)
            {
                if (t.Id == ctx.RoundWindTileId) hits.Add(new(Yaku.YakuhaiRound, 1));
                if (t.Id == ctx.SeatWindTileId) hits.Add(new(Yaku.YakuhaiSeat, 1));
            }
        }
    }

    private static bool CheckSanshokuDoujun(Decomposition d)
    {
        var runStarts = d.Groups.Where(g => g.Kind == GroupKind.Run).Select(g => (int)g.First.Id).ToList();
        for (int n = 0; n < 7; n++)
        {
            bool m = runStarts.Contains(n);
            bool p = runStarts.Contains(9 + n);
            bool s = runStarts.Contains(18 + n);
            if (m && p && s) return true;
        }
        return false;
    }

    private static bool CheckSanshokuDoukou(Decomposition d)
    {
        var trios = d.Groups
            .Where(g => g.Kind is GroupKind.Triplet or GroupKind.Kan)
            .Select(g => (int)g.First.Id).ToList();
        for (int n = 0; n < 9; n++)
        {
            bool m = trios.Contains(n);
            bool p = trios.Contains(9 + n);
            bool s = trios.Contains(18 + n);
            if (m && p && s) return true;
        }
        return false;
    }

    private static bool CheckIttsu(Decomposition d)
    {
        var runStarts = d.Groups.Where(g => g.Kind == GroupKind.Run).Select(g => (int)g.First.Id).ToHashSet();
        for (int suit = 0; suit < 3; suit++)
        {
            int lo = suit * 9;
            if (runStarts.Contains(lo) && runStarts.Contains(lo + 3) && runStarts.Contains(lo + 6))
                return true;
        }
        return false;
    }

    private static bool CheckToitoi(Decomposition d)
    {
        if (d.Form != DecompositionForm.Standard) return false;
        foreach (var g in d.Sets)
            if (g.Kind == GroupKind.Run) return false;
        return true;
    }

    private static bool CheckShousangen(Decomposition d)
    {
        int dragonTriplets = d.Groups.Count(g =>
            (g.Kind is GroupKind.Triplet or GroupKind.Kan) && g.First.IsDragon);
        bool dragonPair = d.Groups.Any(g => g.Kind == GroupKind.Pair && g.First.IsDragon);
        return dragonTriplets == 2 && dragonPair;
    }

    private static bool CheckHonroutou(Decomposition d)
    {
        // All tiles are terminals or honors. For runs — impossible (runs always have simples).
        foreach (var g in d.Groups)
        {
            if (g.Kind == GroupKind.Run) return false;
            if (!g.First.IsTerminalOrHonor) return false;
        }
        return true;
    }

    private static bool CheckChantaLike(Decomposition d, bool junchan)
    {
        // Every group (incl. pair) contains a terminal or honor.
        // Junchan additionally forbids honors anywhere in the hand.
        // At least one run must exist — all-triplet/pair hands are honroutou/chinroutou instead.
        bool hasRun = false;
        foreach (var g in d.Groups)
        {
            if (!g.ContainsTerminalOrHonor) return false;
            if (junchan && g.First.IsHonor) return false;   // non-run honor group
            if (g.Kind == GroupKind.Run) hasRun = true;
        }
        return hasRun;
    }

    private static bool CheckRyanpeikou(Decomposition d)
    {
        if (d.Form != DecompositionForm.Standard) return false;
        var runs = d.Groups.Where(g => g.Kind == GroupKind.Run).Select(g => g.First.Id).ToList();
        if (runs.Count != 4) return false;
        runs.Sort();
        return runs[0] == runs[1] && runs[2] == runs[3] && runs[0] != runs[2];
    }

    private static bool CheckHonitsu(Decomposition d)
    {
        if (d.Form == DecompositionForm.Kokushi) return false;
        int? suit = null;
        bool hasHonor = false;
        foreach (var g in d.Groups)
        {
            var t = g.First;
            if (t.IsHonor) { hasHonor = true; continue; }
            int s = (int)t.Suit;
            if (suit is null) suit = s;
            else if (suit != s) return false;
        }
        return suit is not null && hasHonor;
    }

    private static bool CheckChinitsu(Decomposition d)
    {
        if (d.Form == DecompositionForm.Kokushi) return false;
        int? suit = null;
        foreach (var g in d.Groups)
        {
            var t = g.First;
            if (t.IsHonor) return false;
            int s = (int)t.Suit;
            if (suit is null) suit = s;
            else if (suit != s) return false;
        }
        return suit is not null;
    }

    private static bool CheckDaisangen(Decomposition d)
    {
        int cnt = d.Groups.Count(g => (g.Kind is GroupKind.Triplet or GroupKind.Kan)
                                       && g.First.IsDragon);
        return cnt == 3;
    }

    private static bool CheckFourWinds(Decomposition d, out bool big)
    {
        int windTrips = d.Groups.Count(g => (g.Kind is GroupKind.Triplet or GroupKind.Kan)
                                             && g.First.IsWind);
        bool windPair = d.Groups.Any(g => g.Kind == GroupKind.Pair && g.First.IsWind);

        if (windTrips == 4) { big = true; return true; }
        if (windTrips == 3 && windPair) { big = false; return true; }
        big = false;
        return false;
    }

    private static bool CheckTsuuiisou(Decomposition d)
    {
        foreach (var g in d.Groups)
            if (!g.First.IsHonor) return false;
        return true;
    }

    private static bool CheckChinroutou(Decomposition d)
    {
        foreach (var g in d.Groups)
        {
            if (g.Kind == GroupKind.Run) return false;
            if (g.First.IsHonor) return false;
            if (!g.First.IsTerminal) return false;
        }
        return true;
    }

    private static bool CheckRyuuiisou(Decomposition d)
    {
        // Allowed tiles: 2s, 3s, 4s, 6s, 8s (id 19,20,21,23,25) and 6z=hatsu (id 32).
        ReadOnlySpan<int> allowed = [19, 20, 21, 23, 25, 32];
        foreach (var g in d.Groups)
        {
            foreach (var t in g.Tiles)
            {
                bool ok = false;
                foreach (int a in allowed) if (a == t.Id) { ok = true; break; }
                if (!ok) return false;
            }
        }
        return true;
    }

    private static bool CheckChuuren(Decomposition d, WinContext ctx, out bool pureChuuren)
    {
        pureChuuren = false;
        if (!d.IsMenzen) return false;

        // Needs to be single-suit closed form: 1112345678999 + one extra of same suit.
        // Reconstruct the full closed count from the decomposition.
        var counts = new int[Tile.Count34];
        foreach (var g in d.Groups)
            foreach (var t in g.Tiles)
                counts[t.Id]++;

        int? suit = null;
        for (int i = 0; i < Tile.Count34; i++)
        {
            if (counts[i] == 0) continue;
            if (i >= 27) return false;
            int s = i / 9;
            if (suit is null) suit = s; else if (suit != s) return false;
        }
        if (suit is null) return false;

        int lo = suit.Value * 9;
        // Required base pattern: 3,1,1,1,1,1,1,1,3 at lo..lo+8; plus one extra somewhere.
        ReadOnlySpan<int> baseline = [3, 1, 1, 1, 1, 1, 1, 1, 3];
        int totalExtra = 0;
        for (int i = 0; i < 9; i++)
        {
            int diff = counts[lo + i] - baseline[i];
            if (diff < 0) return false;
            totalExtra += diff;
        }
        if (totalExtra != 1) return false;

        // Pure chuuren: the winning tile is the "extra" — i.e., before drawing the winning
        // tile, the hand was exactly 3,1,1,1,1,1,1,1,3. Check that removing the winning
        // tile leaves the baseline.
        var winId = ctx.WinningTile.Id;
        if (winId < lo || winId >= lo + 9) return false;
        counts[winId]--;
        bool pure = true;
        for (int i = 0; i < 9; i++)
            if (counts[lo + i] != baseline[i]) { pure = false; break; }
        pureChuuren = pure;
        return true;
    }
}
