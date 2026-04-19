namespace DomanMahjongAI.Engine;

/// <summary>
/// Given a 14-tile agari hand (closed counts + open melds = 14 tiles), enumerate
/// every valid decomposition into:
///   - 4 sets + 1 pair (Standard form)
///   - 7 distinct pairs (Chiitoitsu, closed only)
///   - 13 terminals/honors + pair (Kokushi, closed only)
///
/// Open melds are fixed (always contribute one set group each).
/// The winning tile is attributed to whichever closed group contained it
/// — required for fu (tanki/penchan/kanchan) and for concealed-triplet
/// reclassification on ron (sanankou rule).
/// </summary>
public static class HandDecomposer
{
    public static IReadOnlyList<Decomposition> Enumerate(Hand hand, WinContext ctx)
    {
        int closedCount = hand.ClosedTileCount;
        int meldCount = hand.OpenMelds.Count;
        if (closedCount + meldCount * 3 != 14 && !HasKanOnly(hand, meldCount))
            throw new ArgumentException(
                $"decomposer requires a 14-tile agari hand (closed={closedCount}, melds={meldCount})");

        bool isMenzen = hand.OpenMelds.All(m => m.Kind == MeldKind.AnKan);
        bool winFromOpp = ctx.Kind == WinKind.Ron;

        var decomps = new List<Decomposition>();

        // --- Chiitoitsu (closed only, no open melds at all, and no ankans either) ---
        if (meldCount == 0)
        {
            var chitoi = TryChiitoitsu(hand, ctx);
            if (chitoi is not null) decomps.Add(chitoi);

            var kokushi = TryKokushi(hand, ctx);
            if (kokushi is not null) decomps.Add(kokushi);
        }

        // --- Standard ---
        foreach (var d in EnumerateStandard(hand, ctx, isMenzen, winFromOpp))
            decomps.Add(d);

        return decomps;
    }

    private static bool HasKanOnly(Hand hand, int meldCount)
    {
        // A hand with kans has 14 closed tiles when hand + kan replacements are considered.
        // For scoring we treat kans as sets (4 tiles count as 3 for the 14-tile shape).
        return hand.ClosedTileCount + meldCount * 3 == 14;
    }

    // ------------------- Chiitoitsu -------------------

    private static Decomposition? TryChiitoitsu(Hand hand, WinContext ctx)
    {
        var counts = hand.ClosedCounts;
        if (hand.ClosedTileCount != 14) return null;

        var groups = new List<Group>(7);
        int distinctPairs = 0;
        for (int i = 0; i < Tile.Count34; i++)
        {
            if (counts[i] == 0) continue;
            if (counts[i] != 2) return null;
            distinctPairs++;
            bool completedByWin = ctx.WinningTile.Id == i;
            groups.Add(new Group(GroupKind.Pair, Tile.FromId(i),
                                 IsOpen: false, IsCompletedByWinningTile: completedByWin));
        }
        if (distinctPairs != 7) return null;

        return new Decomposition(
            DecompositionForm.Chiitoitsu,
            groups,
            IsMenzen: true,
            WinningTile: ctx.WinningTile,
            WinningTileFromOpponent: ctx.Kind == WinKind.Ron);
    }

    // ------------------- Kokushi -------------------

    private static Decomposition? TryKokushi(Hand hand, WinContext ctx)
    {
        var counts = hand.ClosedCounts;
        if (hand.ClosedTileCount != 14) return null;

        ReadOnlySpan<int> yaochuu = [0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33];
        int distinct = 0, pair = 0, total = 0;
        foreach (int idx in yaochuu)
        {
            int c = counts[idx];
            total += c;
            if (c >= 1) distinct++;
            if (c == 2) pair++;
            if (c > 2) return null;
        }
        if (distinct != 13 || pair != 1 || total != 14) return null;

        return new Decomposition(
            DecompositionForm.Kokushi,
            [],
            IsMenzen: true,
            WinningTile: ctx.WinningTile,
            WinningTileFromOpponent: ctx.Kind == WinKind.Ron);
    }

    // ------------------- Standard -------------------

    private static IEnumerable<Decomposition> EnumerateStandard(Hand hand, WinContext ctx,
                                                                bool isMenzen, bool winFromOpp)
    {
        var winTile = ctx.WinningTile;

        // Fixed groups from open melds (in order).
        var fixedGroups = new List<Group>(hand.OpenMelds.Count);
        foreach (var m in hand.OpenMelds)
            fixedGroups.Add(Group.FromMeld(m, completedByWin: false));

        int setsNeeded = 4 - fixedGroups.Count;
        if (setsNeeded < 0) yield break;

        int closedSum = hand.ClosedTileCount;
        int expectedClosed = setsNeeded * 3 + 2;
        if (closedSum != expectedClosed) yield break;

        var counts = hand.CloneCounts();

        // Try each possible pair (head).
        for (int pairId = 0; pairId < Tile.Count34; pairId++)
        {
            if (counts[pairId] < 2) continue;

            counts[pairId] -= 2;
            var acc = new List<Group>(setsNeeded);
            foreach (var dec in ExtractSets(counts, 0, setsNeeded, acc))
            {
                var final = new List<Group>(5);
                final.AddRange(fixedGroups);
                final.AddRange(dec);

                var pairGroup = new Group(
                    GroupKind.Pair, Tile.FromId(pairId),
                    IsOpen: false,
                    IsCompletedByWinningTile: winTile.Id == pairId);
                final.Add(pairGroup);

                // If the pair is the winning-tile group, all other groups must not also be marked.
                // Otherwise find the first closed set containing the winning tile and mark it.
                var attributed = AttributeWinningTile(final, winTile, fixedGroups.Count);

                yield return new Decomposition(
                    DecompositionForm.Standard,
                    attributed,
                    IsMenzen: isMenzen,
                    WinningTile: winTile,
                    WinningTileFromOpponent: winFromOpp);
            }
            counts[pairId] += 2;
        }
    }

    /// <summary>
    /// Recursive enumerator: extract <paramref name="setsNeeded"/> sets from
    /// <paramref name="counts"/> starting at <paramref name="pos"/>. Yields every distinct
    /// list of Group (triplet/run) that fully consumes 3*setsNeeded tiles.
    /// </summary>
    private static IEnumerable<List<Group>> ExtractSets(int[] counts, int pos, int setsNeeded, List<Group> acc)
    {
        if (setsNeeded == 0)
        {
            // Ensure counts are fully consumed.
            for (int i = 0; i < Tile.Count34; i++)
                if (counts[i] != 0) yield break;

            yield return [.. acc];
            yield break;
        }

        while (pos < Tile.Count34 && counts[pos] == 0) pos++;
        if (pos >= Tile.Count34) yield break;

        // Try triplet at pos.
        if (counts[pos] >= 3)
        {
            counts[pos] -= 3;
            acc.Add(new Group(GroupKind.Triplet, Tile.FromId(pos), IsOpen: false));
            foreach (var r in ExtractSets(counts, pos, setsNeeded - 1, acc)) yield return r;
            acc.RemoveAt(acc.Count - 1);
            counts[pos] += 3;
        }

        // Try run starting at pos (suited only, pos in 0..6 mod 9).
        bool isHonor = pos >= 27;
        bool canRun = !isHonor && (pos % 9) <= 6
                      && counts[pos + 1] > 0 && counts[pos + 2] > 0
                      && counts[pos] > 0;
        if (canRun)
        {
            counts[pos]--; counts[pos + 1]--; counts[pos + 2]--;
            acc.Add(new Group(GroupKind.Run, Tile.FromId(pos), IsOpen: false));
            foreach (var r in ExtractSets(counts, pos, setsNeeded - 1, acc)) yield return r;
            acc.RemoveAt(acc.Count - 1);
            counts[pos]++; counts[pos + 1]++; counts[pos + 2]++;
        }
    }

    /// <summary>
    /// Mark the first closed group that contains the winning tile as IsCompletedByWinningTile.
    /// Skip the first <paramref name="openCount"/> fixed groups. If the pair was already
    /// attributed (IsCompletedByWinningTile=true), leave the set attribution empty
    /// (tanki wait). Otherwise prefer later groups (tanki/penchan/kanchan ambiguity is
    /// resolved at fu time by picking the fu-maximizing attribution; this is a first pass).
    /// </summary>
    private static IReadOnlyList<Group> AttributeWinningTile(
        List<Group> groups, Tile winTile, int openCount)
    {
        // If pair is already attributed, nothing more to do.
        bool pairAttributed = groups[^1].IsCompletedByWinningTile;
        if (pairAttributed) return groups;

        for (int i = openCount; i < groups.Count - 1; i++)  // skip fixed groups and the pair
        {
            if (groups[i].ContainsTile(winTile))
            {
                var g = groups[i];
                groups[i] = g with { IsCompletedByWinningTile = true };
                break;
            }
        }
        return groups;
    }
}
