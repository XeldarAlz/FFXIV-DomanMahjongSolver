using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class SituationalYakuTests
{
    private static IReadOnlyList<YakuHit> DetectBest(string notation, WinContext ctx,
                                                    IReadOnlyList<Meld>? melds = null)
    {
        var hand = Hand.FromNotation(notation, melds);
        var decomps = HandDecomposer.Enumerate(hand, ctx);
        return decomps
            .Select(d => YakuDetector.Detect(d, ctx))
            .Where(list => list.Count > 0)
            .OrderByDescending(list => list.Any(h => h.IsYakuman) ? 999 : list.Sum(h => h.Han))
            .FirstOrDefault() ?? [];
    }

    private static WinContext Base(string winTile, WinKind kind = WinKind.Tsumo) =>
        new(Tiles.Parse(winTile)[0], kind);

    [Fact]
    public void Double_riichi_reports_as_double_and_suppresses_single_riichi()
    {
        var ctx = Base("2m") with { IsRiichi = true, IsDoubleRiichi = true };
        var hits = DetectBest("234m456p789s234s99m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.DoubleRiichi && h.Han == 2);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Riichi);
    }

    [Fact]
    public void Ippatsu_requires_riichi_or_double_riichi()
    {
        // With riichi: ippatsu counted.
        var withRiichi = Base("2m") with { IsRiichi = true, IsIppatsu = true };
        var hits1 = DetectBest("234m456p789s234s99m", withRiichi);
        Assert.Contains(hits1, h => h.Yaku == Yaku.Ippatsu);

        // Without riichi: ippatsu flag ignored (real games never have one without the other,
        // but we defensively filter).
        var noRiichi = Base("2m") with { IsIppatsu = true };
        var hits2 = DetectBest("234m456p789s234s99m", noRiichi);
        Assert.DoesNotContain(hits2, h => h.Yaku == Yaku.Ippatsu);
    }

    [Fact]
    public void Rinshan_kaihou()
    {
        var ctx = Base("2m") with { IsRinshan = true };
        var hits = DetectBest("234m456p789s234s99m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Rinshan);
    }

    [Fact]
    public void Chankan()
    {
        var ctx = Base("2m", WinKind.Ron) with { IsChankan = true };
        var hits = DetectBest("234m456p789s234s99m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Chankan);
    }

    [Fact]
    public void Haitei_tsumo_last_wall_tile()
    {
        var ctx = Base("2m") with { IsHaitei = true };
        var hits = DetectBest("234m456p789s234s99m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Haitei);
    }

    [Fact]
    public void Houtei_ron_last_discard()
    {
        var ctx = Base("2m", WinKind.Ron) with { IsHoutei = true };
        var hits = DetectBest("234m456p789s234s99m", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Houtei);
    }

    [Fact]
    public void Sanankou_three_concealed_triplets()
    {
        // 111m 222p 333s 456s 55z — 3 concealed triplets + 1 run + 1 pair, closed.
        var hits = DetectBest("111m222p333s456s55z", Base("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Sanankou);
        Assert.DoesNotContain(hits, h => h.IsYakuman);
    }

    [Fact]
    public void Shanpon_ron_downgrades_suuankou_to_sanankou()
    {
        // Closed hand, shanpon wait between 33s and 55s, ron on 3s.
        // Pre-win structure: 111m 222p 555z 33s 55s (13 tiles).
        // Post-win: 111m 222p 555z 333s 55s — 4 triplets + 1 pair.
        // On ron, the 333s triplet is effectively open → 3 ankou not 4 → Sanankou not Suuankou.
        var hand = Hand.FromNotation("111m222p555z333s55s");
        var ctx = new WinContext(Tiles.Parse("3s")[0], WinKind.Ron);
        var result = ScoreEvaluator.Evaluate(hand, ctx);
        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Yaku, h => h.Yaku == Yaku.Suuankou);
        Assert.Contains(result.Yaku, h => h.Yaku == Yaku.Sanankou);
        Assert.Contains(result.Yaku, h => h.Yaku == Yaku.Toitoi);
    }

    [Fact]
    public void Sanshoku_doukou_three_triplets_same_number()
    {
        // 222m 222p 222s + 456p + 55z — need 4 sets + 1 pair.
        // Too many triplets of 2 for a hand... let me check: 3*3=9 + 3 + 2 = 14 ✓
        var hits = DetectBest("222m222p222s456p55z", Base("5z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.SanshokuDoukou);
    }

    [Fact]
    public void Shousangen_two_dragon_triplets_plus_dragon_pair()
    {
        // 555z 666z + 77z pair + two more sets
        var hits = DetectBest("123m456p555z666z77z", Base("7z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Shousangen);
        // Also yakuhai: haku (555z) and hatsu (666z) — each +1 han
        Assert.Contains(hits, h => h.Yaku == Yaku.YakuhaiHaku);
        Assert.Contains(hits, h => h.Yaku == Yaku.YakuhaiHatsu);
    }

    [Fact]
    public void Sankantsu_three_kans()
    {
        var k1 = Meld.AnKan(Tile.FromId(0));   // 1m
        var k2 = Meld.AnKan(Tile.FromId(9));   // 1p
        var k3 = Meld.AnKan(Tile.FromId(18));  // 1s
        var hand = Hand.FromNotation("234s55m", [k1, k2, k3]);
        var hits = HandDecomposer.Enumerate(hand, Base("2s"))
            .SelectMany(d => YakuDetector.Detect(d, Base("2s"))).ToList();
        Assert.Contains(hits, h => h.Yaku == Yaku.Sankantsu);
    }

    [Fact]
    public void Honroutou_with_toitoi_all_terminal_honor_triplets_open_hand()
    {
        // Open pon of 1m breaks menzen → not suuankou, so honroutou + toitoi stand.
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), 2);
        var hand = Hand.FromNotation("999m111z222z99p", [pon]);
        var hits = HandDecomposer.Enumerate(hand, Base("9p"))
            .SelectMany(d => YakuDetector.Detect(d, Base("9p"))).ToList();
        Assert.Contains(hits, h => h.Yaku == Yaku.Honroutou);
        Assert.Contains(hits, h => h.Yaku == Yaku.Toitoi);
        Assert.DoesNotContain(hits, h => h.IsYakuman);
    }

    [Fact]
    public void Open_honitsu_is_two_han_not_three()
    {
        // Honitsu with an open pon → 2 han (one less than closed).
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), 2);  // 1m pon
        var hand = Hand.FromNotation("234m567m111z22z", [pon]);
        var hits = HandDecomposer.Enumerate(hand, Base("2z"))
            .SelectMany(d => YakuDetector.Detect(d, Base("2z"))).ToList();
        var honitsu = hits.First(h => h.Yaku == Yaku.Honitsu);
        Assert.Equal(2, honitsu.Han);
    }

    [Fact]
    public void Open_chinitsu_is_five_han_not_six()
    {
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), 2);  // 1m pon
        var hand = Hand.FromNotation("234m567m789m22m", [pon]);
        var hits = HandDecomposer.Enumerate(hand, Base("2m"))
            .SelectMany(d => YakuDetector.Detect(d, Base("2m"))).ToList();
        var chinitsu = hits.First(h => h.Yaku == Yaku.Chinitsu);
        Assert.Equal(5, chinitsu.Han);
    }

    [Fact]
    public void Open_ittsu_is_one_han_not_two()
    {
        // Open chi of 123m + closed 456m 789m 345p 55z = 9 closed + 3 meld = 12? Wait need 14.
        // chi = 3 tiles, closed needs to be 11 to total 14.
        // 456m 789m 345p 678p 55z = 3+3+3+3+2 = 14 closed. Plus chi makes 17. Too many.
        // Correct: 11 closed + chi. chi of 123m + closed 456m 789m 345p 55z = 3+3+3+2 = 11. Good.
        var chi = Meld.Chi(Tile.FromId(0), Tile.FromId(2), 3);  // chi 123m
        var hand = Hand.FromNotation("456m789m345p55z", [chi]);
        var hits = HandDecomposer.Enumerate(hand, Base("5z"))
            .SelectMany(d => YakuDetector.Detect(d, Base("5z"))).ToList();
        var ittsu = hits.First(h => h.Yaku == Yaku.Ittsu);
        Assert.Equal(1, ittsu.Han);
    }
}
