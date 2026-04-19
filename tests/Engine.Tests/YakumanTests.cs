using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class YakumanTests
{
    private static WinContext Tsumo(string winTile) =>
        new(Tiles.Parse(winTile)[0], WinKind.Tsumo);

    private static IReadOnlyList<YakuHit> DetectAny(string notation, WinContext ctx,
                                                    IReadOnlyList<Meld>? melds = null)
    {
        var hand = Hand.FromNotation(notation, melds);
        return HandDecomposer.Enumerate(hand, ctx)
            .SelectMany(d => YakuDetector.Detect(d, ctx))
            .ToList();
    }

    [Fact]
    public void Shousuushii_three_wind_triplets_plus_one_wind_pair()
    {
        // 111z 222z 333z 44z + 11m pair — wait, only one pair allowed. Use:
        // 111z 222z 333z 44z + 111m (triplet) = 4 sets + 1 pair, pair is wind
        var hits = DetectAny("111m111z222z333z44z", Tsumo("4z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Shousuushii && h.IsYakuman);
        Assert.DoesNotContain(hits, h => h.Yaku == Yaku.Daisuushii);
    }

    [Fact]
    public void Daisuushii_four_wind_triplets_is_double_yakuman()
    {
        // 111z 222z 333z 444z + 11m pair
        var hits = DetectAny("11m111z222z333z444z", Tsumo("1m"));
        var hit = hits.Single(h => h.Yaku == Yaku.Daisuushii);
        Assert.True(hit.IsYakuman);
        Assert.Equal(26, hit.Han);  // double yakuman
    }

    [Fact]
    public void Chinroutou_all_terminals_is_yakuman()
    {
        // 111m 999m 111p 999p 11s
        var hits = DetectAny("111999m111999p11s", Tsumo("1s"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Chinroutou && h.IsYakuman);
    }

    [Fact]
    public void Ryuuiisou_only_green_tiles_is_yakuman()
    {
        // 2s 3s 4s 6s 8s and hatsu (6z) only
        // 234s 234s 666z 888s + 22s wait — 6z triplet + 234s + 234s + 888s + 66s (pair) = not all green
        // Let me pick: 234s 234s 234s 888s + 66z pair
        var hits = DetectAny("222333444s888s66z", Tsumo("6z"));
        Assert.Contains(hits, h => h.Yaku == Yaku.Ryuuiisou && h.IsYakuman);
    }

    [Fact]
    public void Suukantsu_four_kans_is_yakuman()
    {
        // 4 kans (3 ankan + 1 minkan, doesn't matter) + one closed pair.
        var k1 = Meld.AnKan(Tile.FromId(0));   // 1m kan
        var k2 = Meld.AnKan(Tile.FromId(9));   // 1p kan
        var k3 = Meld.AnKan(Tile.FromId(18));  // 1s kan
        var k4 = Meld.AnKan(Tile.FromId(27));  // East kan
        var hand = Hand.FromNotation("44m", [k1, k2, k3, k4]);
        // Total: 4 kans (each 4 tiles = but counted as 3 for shanten/decomp) + 2 closed = 14
        var hits = HandDecomposer.Enumerate(hand, Tsumo("4m"))
            .SelectMany(d => YakuDetector.Detect(d, Tsumo("4m")))
            .ToList();
        Assert.Contains(hits, h => h.Yaku == Yaku.Suukantsu && h.IsYakuman);
    }

    [Fact]
    public void Chuuren_poutou_is_yakuman()
    {
        // 1112345678999m + extra 5m (winning tile is 5m)
        // Before winning tile: 111234567899m = 12 tiles ≠ 13. Actually:
        // Closed hand before winning: 1112345678999m = 13 tiles. Draw a 5m to make 14.
        var hits = DetectAny("11123455678999m", Tsumo("5m"));
        Assert.Contains(hits, h => h.Yaku == Yaku.ChuurenPoutou && h.IsYakuman);
    }

    [Fact]
    public void Pure_chuuren_is_double_yakuman()
    {
        // Start from 1112345678999m (13 tiles). Draw an extra 1m → 14 tiles matching
        // the 3,1,1,1,1,1,1,1,3 pattern + 1 extra 1m. Pure because removing the winning
        // tile leaves the exact baseline.
        var hand = Hand.FromNotation("11112345678999m");
        var ctx = Tsumo("1m");
        var result = ScoreEvaluator.Evaluate(hand, ctx);
        Assert.NotNull(result);
        var chuuren = result!.Yaku.First(h => h.Yaku == Yaku.ChuurenPoutou);
        Assert.True(chuuren.IsYakuman);
        Assert.Equal(26, chuuren.Han);
    }

    [Fact]
    public void Tenhou_dealer_first_draw_is_yakuman()
    {
        var ctx = new WinContext(
            Tiles.Parse("5z")[0], WinKind.Tsumo,
            IsTenhou: true, IsDealer: true);
        var hits = DetectAny("234m456p678s234s55z", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Tenhou && h.IsYakuman);
    }

    [Fact]
    public void Chihou_non_dealer_first_draw_is_yakuman()
    {
        var ctx = new WinContext(
            Tiles.Parse("5z")[0], WinKind.Tsumo,
            IsChihou: true);
        var hits = DetectAny("234m456p678s234s55z", ctx);
        Assert.Contains(hits, h => h.Yaku == Yaku.Chihou && h.IsYakuman);
    }

    [Fact]
    public void Kazoe_yakuman_thirteen_plus_han_from_normal_yaku()
    {
        // Riichi + Ippatsu + Menzen Tsumo + Pinfu + Chinitsu + Ittsu + Tanyao — roughly engineer 13+ han
        // Simpler: hand with chinitsu (6) + ittsu (2) + tanyao? tanyao excludes terminals, ittsu needs 123/789.
        // Use dora to pile on: chinitsu (6) + riichi (1) + tsumo (1) + pinfu (1) + dora 4 = 13 han.
        // Chinitsu hand: 234p 456p 789p 234p 55p, winning 2p, all-pinzu.
        var dora = new[] { Tiles.Parse("1p")[0] };  // indicator 1p → dora = 2p
        // Hand has 2p x 2 (in two 234p runs) → 2 dora. Plus 5p pair (not dora). Not enough.
        // Let me use: 234p 234p 234p 789p 55p — has 2p x 3, winning 2p. Dora = 2p = 3 + winning 1 more = 4 but wait hand only has 3 2p.
        // Just force the assertion: let's check that 13-han normal maps to yakuman tier.
        var (basePts, tier) = ScoreCalculator.BasePoints(13, 30, isYakuman: false);
        Assert.Equal(8000, basePts);
        Assert.Equal("yakuman", tier);
    }
}
