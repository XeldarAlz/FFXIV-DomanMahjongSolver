using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class DoraTests
{
    [Fact]
    public void Dora_indicator_cycles_next_tile_in_suit()
    {
        // Hand has exactly one 2m. Indicator 1m → dora = 2m → +1 han vs. no-dora.
        var hand = Hand.FromNotation("234m456p789s234s99m");
        var baseCtx = new WinContext(Tiles.Parse("2m")[0], WinKind.Tsumo);
        var withDora = baseCtx with { DoraIndicators = [Tiles.Parse("1m")[0]] };

        var r0 = ScoreEvaluator.Evaluate(hand, baseCtx)!;
        var r1 = ScoreEvaluator.Evaluate(hand, withDora)!;
        Assert.Equal(r0.Han + 1, r1.Han);
    }

    [Fact]
    public void Dora_cycles_nine_back_to_one()
    {
        // Indicator 9s → dora = 1s. Hand has two 1s (in two 123s runs).
        var hand = Hand.FromNotation("234m456p123s123s99m");
        var baseCtx = new WinContext(Tiles.Parse("2m")[0], WinKind.Tsumo);
        var withDora = baseCtx with { DoraIndicators = [Tiles.Parse("9s")[0]] };

        var r0 = ScoreEvaluator.Evaluate(hand, baseCtx)!;
        var r1 = ScoreEvaluator.Evaluate(hand, withDora)!;
        Assert.Equal(r0.Han + 2, r1.Han);
    }

    [Fact]
    public void Wind_indicator_cycles_east_south_west_north_east()
    {
        // Indicator N (30) → dora = E (27). Pair 11z = two E → +2 han.
        var hand = Hand.FromNotation("234m456p789s234s11z");
        var baseCtx = new WinContext(Tiles.Parse("2m")[0], WinKind.Tsumo,
                                      RoundWindTileId: 28, SeatWindTileId: 28);
        var withDora = baseCtx with { DoraIndicators = [Tiles.Parse("4z")[0]] };

        var r0 = ScoreEvaluator.Evaluate(hand, baseCtx)!;
        var r1 = ScoreEvaluator.Evaluate(hand, withDora)!;
        Assert.Equal(r0.Han + 2, r1.Han);
    }

    [Fact]
    public void Dragon_indicator_cycles_haku_hatsu_chun_haku()
    {
        // Indicator chun (33) → dora = haku (31). Pair 55z = two haku → +2 han.
        var hand = Hand.FromNotation("234m456p789s234s55z");
        var baseCtx = new WinContext(Tiles.Parse("2m")[0], WinKind.Tsumo);
        var withDora = baseCtx with { DoraIndicators = [Tiles.Parse("7z")[0]] };

        var r0 = ScoreEvaluator.Evaluate(hand, baseCtx)!;
        var r1 = ScoreEvaluator.Evaluate(hand, withDora)!;
        Assert.Equal(r0.Han + 2, r1.Han);
    }

    [Fact]
    public void Ura_dora_counted_only_with_riichi_and_menzen()
    {
        // Hand has exactly one 2m. Ura-indicator 1m → ura 2m → +1 han when riichi.
        var hand = Hand.FromNotation("234m456p789s234s99m");
        var withoutRiichi = new WinContext(
            Tiles.Parse("2m")[0], WinKind.Tsumo,
            UraDoraIndicators: [Tiles.Parse("1m")[0]]);
        var withRiichi = withoutRiichi with { IsRiichi = true };

        var r1 = ScoreEvaluator.Evaluate(hand, withoutRiichi)!;
        var r2 = ScoreEvaluator.Evaluate(hand, withRiichi)!;

        // r2 adds riichi (+1) and ura dora (+1) compared to r1.
        Assert.Equal(r1.Han + 2, r2.Han);
    }
}
