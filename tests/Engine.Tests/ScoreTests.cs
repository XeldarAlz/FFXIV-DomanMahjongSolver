using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class ScoreTests
{
    [Fact]
    public void Non_dealer_mangan_ron_is_8000()
    {
        // 5-han mangan
        var (basePts, tier) = ScoreCalculator.BasePoints(han: 5, fu: 30, isYakuman: false);
        Assert.Equal(2000, basePts);
        Assert.Equal("mangan", tier);
        var pay = ScoreCalculator.Pay(basePts, isDealer: false, WinKind.Ron);
        Assert.Equal(8000, pay.RonTotal);
        Assert.Equal(8000, pay.Total);
    }

    [Fact]
    public void Dealer_mangan_ron_is_12000()
    {
        var (basePts, _) = ScoreCalculator.BasePoints(5, 30, false);
        var pay = ScoreCalculator.Pay(basePts, isDealer: true, WinKind.Ron);
        Assert.Equal(12000, pay.RonTotal);
    }

    [Fact]
    public void Non_dealer_tsumo_30fu_3han_is_2000_total()
    {
        // base = 30 * 2^(3+2) = 960
        var (basePts, _) = ScoreCalculator.BasePoints(3, 30, false);
        Assert.Equal(960, basePts);
        var pay = ScoreCalculator.Pay(basePts, isDealer: false, WinKind.Tsumo);
        Assert.Equal(2000, pay.DealerPay);     // ceil(960*2 / 100) * 100 = 2000
        Assert.Equal(1000, pay.NonDealerPay);  // ceil(960 / 100) * 100 = 1000
        Assert.Equal(4000, pay.Total);         // 2000 + 1000 + 1000
    }

    [Fact]
    public void Dealer_tsumo_30fu_3han_is_6000_total()
    {
        var (basePts, _) = ScoreCalculator.BasePoints(3, 30, false);
        var pay = ScoreCalculator.Pay(basePts, isDealer: true, WinKind.Tsumo);
        Assert.Equal(2000, pay.NonDealerPay);  // ceil(960*2/100) * 100 = 2000 each
        Assert.Equal(6000, pay.Total);         // 2000 × 3
    }

    [Fact]
    public void Yakuman_non_dealer_ron_is_32000()
    {
        var (basePts, tier) = ScoreCalculator.BasePoints(13, 40, isYakuman: true);
        Assert.Equal(8000, basePts);
        Assert.Equal("yakuman", tier);
        var pay = ScoreCalculator.Pay(basePts, isDealer: false, WinKind.Ron);
        Assert.Equal(32000, pay.RonTotal);
    }

    [Fact]
    public void Yakuman_dealer_tsumo_is_48000()
    {
        var (basePts, _) = ScoreCalculator.BasePoints(13, 40, true);
        var pay = ScoreCalculator.Pay(basePts, isDealer: true, WinKind.Tsumo);
        Assert.Equal(16000, pay.NonDealerPay);
        Assert.Equal(48000, pay.Total);
    }

    [Fact]
    public void Double_yakuman_non_dealer_ron_is_64000()
    {
        var (basePts, _) = ScoreCalculator.BasePoints(26, 40, true);
        Assert.Equal(16000, basePts);
        var pay = ScoreCalculator.Pay(basePts, isDealer: false, WinKind.Ron);
        Assert.Equal(64000, pay.RonTotal);
    }

    [Fact]
    public void Haneman_6han_2han_value()
    {
        var (basePts, tier) = ScoreCalculator.BasePoints(6, 30, false);
        Assert.Equal(3000, basePts);
        Assert.Equal("haneman", tier);
    }

    [Fact]
    public void Low_han_low_fu_non_mangan()
    {
        // 20 fu × 2^(1+2) = 160. Below 2000. Not mangan.
        var (basePts, tier) = ScoreCalculator.BasePoints(1, 20, false);
        Assert.Equal(160, basePts);
        Assert.Equal("", tier);
    }

    [Fact]
    public void Evaluator_detects_riichi_pinfu_tsumo_and_pays_correct_amount()
    {
        // 234m 456p 789s 234s 99m — 99m and 9s break tanyao; wait on 2m is ryanmen.
        var hand = Hand.FromNotation("234m456p789s234s99m");
        var ctx = new WinContext(
            WinningTile: Tiles.Parse("2m")[0],
            Kind: WinKind.Tsumo,
            IsRiichi: true,
            IsDealer: false);

        var result = ScoreEvaluator.Evaluate(hand, ctx);
        Assert.NotNull(result);
        Assert.Contains(result!.Yaku, h => h.Yaku == Yaku.Riichi);
        Assert.Contains(result.Yaku, h => h.Yaku == Yaku.MenzenTsumo);
        Assert.Contains(result.Yaku, h => h.Yaku == Yaku.Pinfu);
        Assert.Equal(3, result.Han);        // riichi + tsumo + pinfu + 0 dora
        Assert.Equal(20, result.Fu);        // pinfu tsumo
        // base = 20 * 2^(3+2) = 640
        // non-dealer tsumo: dealer 1280→1300, each non-dealer 640→700, total 1300+700+700=2700
        Assert.Equal(1300, result.Payments.DealerPay);
        Assert.Equal(700, result.Payments.NonDealerPay);
        Assert.Equal(2700, result.Payments.Total);
    }

    [Fact]
    public void Evaluator_returns_null_when_no_yaku()
    {
        // Winning shape with no yaku at all: 123m 456p 789s 22m ron from opponent, no riichi, not closed on anything special.
        // Must be open (so no menzen tsumo possible) and no yaku-triggering tiles.
        // Simplest: pon a random non-yakuhai, hand has only runs and a simple pair. This gives no yaku.
        var pon = Meld.Pon(Tile.FromId(3), Tile.FromId(3), fromSeat: 2);  // 4m pon
        var hand = Hand.FromNotation("123m456p789s22s", [pon]);
        var ctx = new WinContext(Tiles.Parse("2s")[0], WinKind.Ron, IsDealer: false);
        var result = ScoreEvaluator.Evaluate(hand, ctx);
        Assert.Null(result);
    }
}
