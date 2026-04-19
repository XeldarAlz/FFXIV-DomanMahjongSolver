using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class YakuTests
{
    private static IReadOnlyList<YakuHit> DetectBest(string notation, WinContext ctx, IReadOnlyList<Meld>? melds = null)
    {
        var hand = Hand.FromNotation(notation, melds);
        var decomps = HandDecomposer.Enumerate(hand, ctx);
        // Pick the decomposition that yields the most han (dora-excluded, yakuman preferred).
        var best = decomps
            .Select(d => YakuDetector.Detect(d, ctx))
            .Where(list => list.Count > 0)
            .OrderByDescending(list => list.Any(h => h.IsYakuman) ? 999 : list.Sum(h => h.Han))
            .FirstOrDefault();
        return best ?? [];
    }

    private static WinContext Tsumo(string winTile, bool dealer = false) =>
        new(Tiles.Parse(winTile)[0], WinKind.Tsumo, IsDealer: dealer);

    private static WinContext Ron(string winTile, bool dealer = false) =>
        new(Tiles.Parse(winTile)[0], WinKind.Ron, IsDealer: dealer);

    [Fact]
    public void Pinfu_closed_all_runs_ryanmen_wait_menzen_tsumo()
    {
        // 234m 456p 678s 234s 55m — all runs + non-yakuhai 5m pair; winning 2m = ryanmen
        var hits = DetectBest("234m456p678s234s55m", Tsumo("2m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Pinfu);
        Assert.Contains(hits, h => h.Yaku == Yaku.MenzenTsumo);
    }

    [Fact]
    public void Pinfu_rejected_on_kanchan_wait()
    {
        // 13m winning on 2m = kanchan → not pinfu
        var hits = DetectBest("123m456p678s234s55z", Tsumo("2m"));
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Pinfu);
    }

    [Fact]
    public void Tanyao_no_terminals_or_honors()
    {
        // 234m 456p 678s 234s 55s — all simples
        var hits = DetectBest("234m456p678s234s55s", Tsumo("2m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Tanyao);
    }

    [Fact]
    public void Yakuhai_haku_triplet()
    {
        // 123m 456p 789s 5z5z5z 44m → pon'd haku? or closed triplet.
        // Build closed: 123m 456p 789s 555z 44m = 14 tiles with 555z + 44m pair
        var hits = DetectBest("123m456p789s555z44m", Tsumo("4m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.YakuhaiHaku);
    }

    [Fact]
    public void Yakuhai_round_wind_triplet()
    {
        // Round wind = East (27). Triplet of East.
        var ctx = Tsumo("4m") with { RoundWindTileId = 27, SeatWindTileId = 28 };
        var hits = DetectBest("123m456p789s111z44m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.YakuhaiRound);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.YakuhaiSeat);
    }

    [Fact]
    public void Toitoi_all_triplets_open_hand()
    {
        // 4 triplets + pair. Must be open — else it's suuankou (yakuman).
        // Open pon of 1m + closed 444p 777s 222z 33z = 11 closed + 3 meld = 14.
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), fromSeat: 2);
        var hand = Hand.FromNotation("444p777s222z33z", [pon]);
        var decomps = HandDecomposer.Enumerate(hand, Tsumo("3z"));
        var hits = decomps.SelectMany(d => YakuDetector.Detect(d, Tsumo("3z"))).ToList();
        Assert.Contains(hits, h => h.Yaku == Yaku.Toitoi);
        Assert.DoesNotContain(hits, h => h.IsYakuman);
    }

    [Fact]
    public void Iipeiko_two_identical_runs()
    {
        // 123m 123m 456p 789s 55z
        var hits = DetectBest("112233m456p789s55z", Tsumo("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Iipeiko);
    }

    [Fact]
    public void Ryanpeikou_two_pairs_of_identical_runs()
    {
        // 123m 123m 456p 456p 55z — ryanpeikou, closed hand
        var hits = DetectBest("112233m445566p55z", Tsumo("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Ryanpeikou);
        // Iipeiko must not also be reported
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Iipeiko);
    }

    [Fact]
    public void Chiitoitsu_seven_pairs()
    {
        var hits = DetectBest("1122m3344p5566s77z", Tsumo("7z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Chiitoitsu);
    }

    [Fact]
    public void Honitsu_one_suit_plus_honors()
    {
        // All manzu + honors
        var hits = DetectBest("123m456m789m111z22z", Tsumo("2z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Honitsu);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Chinitsu);
    }

    [Fact]
    public void Chinitsu_one_suit_no_honors()
    {
        // Pure manzu
        var hits = DetectBest("123m456m789m11223m", Tsumo("3m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Chinitsu);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Honitsu);
    }

    [Fact]
    public void Ittsu_one_to_nine_straight()
    {
        // 123m 456m 789m + 456p + 11z
        var hits = DetectBest("123456789m456p11z", Tsumo("1z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Ittsu);
    }

    [Fact]
    public void SanshokuDoujun_same_run_three_suits()
    {
        // 123m 123p 123s 789s 55z
        var hits = DetectBest("123m123p123s789s55z", Tsumo("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.SanshokuDoujun);
    }

    [Fact]
    public void Chanta_every_group_has_terminal_or_honor()
    {
        // 123m 789p 123s 789s 11z — every group contains a terminal (at least one run, so not honroutou)
        // Pair 11z is an honor, so chanta not junchan
        var hits = DetectBest("123m789p123s789s11z", Tsumo("1z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Chanta);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Junchan);
    }

    [Fact]
    public void Junchan_terminals_only_no_honors()
    {
        // 123m 789p 123s 789s 99m — every group has terminal, no honors
        var hits = DetectBest("123m789p123s789s99m", Tsumo("9m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Junchan);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Chanta);
    }

    [Fact]
    public void Kokushi_musou_is_yakuman()
    {
        var hits = DetectBest("19m19p19s1234567z1m", Tsumo("1m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Kokushi && h.IsYakuman);
    }

    [Fact]
    public void Suuankou_four_concealed_triplets_is_yakuman()
    {
        // 111m 222m 333p 444s 55z — all concealed (tsumo)
        var hits = DetectBest("111m222m333p444s55z", Tsumo("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Suuankou && h.IsYakuman);
    }

    [Fact]
    public void Daisangen_three_dragon_triplets()
    {
        // 555z 666z 777z + 123m + 44p
        var hits = DetectBest("123m44p555z666z777z", Tsumo("4p"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Daisangen && h.IsYakuman);
    }

    [Fact]
    public void Tsuuiisou_all_honors()
    {
        // 4 triplets of honors + 1 pair of honor
        var hits = DetectBest("111z222z333z444z55z", Tsumo("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Tsuuiisou && h.IsYakuman);
    }

    [Fact]
    public void Yakuman_does_not_combine_with_normal_yaku()
    {
        // Kokushi — should only report kokushi, not, e.g., "Honroutou"
        var hits = DetectBest("19m19p19s1234567z1m", Tsumo("1m"));
        Assert.All(hits, h => Assert.True(h.IsYakuman));
    }

    [Fact]
    public void Riichi_plus_menzen_tsumo_plus_pinfu()
    {
        var ctx = Tsumo("2m") with { IsRiichi = true };
        var hits = DetectBest("234m456p678s234s55m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Riichi);
        Assert.Contains(hits, h => h.Yaku == Yaku.MenzenTsumo);
        Assert.Contains(hits, h => h.Yaku == Yaku.Pinfu);
    }

    [Fact]
    public void Open_hand_breaks_menzen_yaku()
    {
        // 234m 456p 678s 234s 55z but pon'd one of the groups
        // Make the open decomposition: pon of 4s + closed 234m 456p 678s 5s5z — hmm complicated.
        // Simpler: pon of 5z (pair dragon) → breaks menzen.
        // Closed: 234m 456p 678s 234s = 12 tiles + pon 5z doesn't work (pon = 3 tiles, 5z is just a honor).
        // Let me use pon of 4s + closed 234m 456p 678s 5z5z: 3+3+3+2 = 11 closed + 3 meld = 14. OK.
        var pon = Meld.Pon(Tile.FromId(21), Tile.FromId(21), fromSeat: 2);  // 4s triplet
        var hand = Hand.FromNotation("234m456p678s55z", [pon]);
        var ctx = Tsumo("5z");
        var decomps = HandDecomposer.Enumerate(hand, ctx);
        var hits = decomps.SelectMany(d => YakuDetector.Detect(d, ctx)).ToList();

        // No menzen tsumo, no riichi, no pinfu allowed because hand is open
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.MenzenTsumo);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Pinfu);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Iipeiko);
    }
}
