using Xunit;

namespace DomanMahjongAI.Engine.Tests;

public class FuTests
{
    private static Decomposition Decomp(string notation, WinContext ctx,
                                        IReadOnlyList<Meld>? melds = null)
    {
        var hand = Hand.FromNotation(notation, melds);
        return HandDecomposer.Enumerate(hand, ctx)
            .First(d => d.Form == DecompositionForm.Standard);
    }

    private static WinContext Tsumo(string winTile) => new(Tiles.Parse(winTile)[0], WinKind.Tsumo);
    private static WinContext Ron(string winTile) => new(Tiles.Parse(winTile)[0], WinKind.Ron);

    [Fact]
    public void Chiitoitsu_is_25_fu_flat()
    {
        var hand = Hand.FromNotation("1122m3344p5566s77z");
        var ctx = Ron("7z");
        var d = HandDecomposer.Enumerate(hand, ctx)
            .Single(x => x.Form == DecompositionForm.Chiitoitsu);
        Assert.Equal(25, FuCalculator.Compute(d, ctx, isPinfu: false));
    }

    [Fact]
    public void Pinfu_tsumo_is_20_fu_flat()
    {
        var ctx = Tsumo("2m");
        var d = Decomp("234m456p789s234s99m", ctx);
        Assert.Equal(20, FuCalculator.Compute(d, ctx, isPinfu: true));
    }

    [Fact]
    public void Pinfu_ron_is_30_fu_flat()
    {
        var ctx = Ron("2m");
        var d = Decomp("234m456p789s234s99m", ctx);
        Assert.Equal(30, FuCalculator.Compute(d, ctx, isPinfu: true));
    }

    [Fact]
    public void Menzen_ron_adds_ten_fu()
    {
        // Closed hand, ron, no pinfu. Expect base 20 + 10 menzen kafu + group fu, rounded up.
        // Hand: 111m 234p 234s 789s 55z, ron on 9s (completing 789s ryanmen? actually winning 9s with 78 = ryanmen-penchan? 789s has 9 at tail, if first=7, firstMod=6, offset=2, so penchan? Let me pick a simpler ron setup.)
        // Use a hand where completing group is a simple run (ryanmen) — no wait fu.
        // Hand: 234m 456p 678s 111z 55p, ron on 2m.
        var ctx = Ron("2m");
        var d = Decomp("234m456p678s111z55p", ctx);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 10 menzen kafu + 0 tsumo + 111z closed triplet terminal = 8 + 0 wait (ryanmen) = 38 → round up to 40.
        Assert.Equal(40, fu);
    }

    [Fact]
    public void Closed_triplet_simple_is_four_fu()
    {
        // 222p closed triplet + 3 runs + pair, tsumo (so 2 fu + base 20), no menzen kafu.
        var ctx = Tsumo("5z");
        var d = Decomp("234m222p789s234s55z", ctx);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 2 tsumo + 4 (222p closed simple triplet) + 2 (55z pair dragon/yakuhai? 5z=haku dragon → +2) = 28 → 30.
        Assert.Equal(30, fu);
    }

    [Fact]
    public void Closed_kan_terminal_is_32_fu()
    {
        var ankan = Meld.AnKan(Tile.FromId(0));  // 1m ankan
        var ctx = Tsumo("4m");
        var hand = Hand.FromNotation("234m456p789s44m", [ankan]);
        var d = HandDecomposer.Enumerate(hand, ctx).First(x => x.Form == DecompositionForm.Standard);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 2 tsumo + 32 (1m ankan terminal) = 54 → 60
        Assert.Equal(60, fu);
    }

    [Fact]
    public void Tanki_wait_adds_two_fu()
    {
        // Closed hand, tsumo, pair-wait on 5z.
        var ctx = Tsumo("5z");
        // 3 runs + 1 closed triplet + pair tanki
        var d = Decomp("234m456p789s111z55z", ctx);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 2 tsumo + 8 (111z closed terminal triplet) + 2 (5z haku yakuhai pair) + 2 tanki = 34 → 40
        Assert.Equal(40, fu);
    }

    [Fact]
    public void Kanchan_wait_adds_two_fu()
    {
        // Winning on 5m completing 4-5-6m from 4m-6m kanchan.
        var ctx = Tsumo("5m");
        // 4m-5m-6m run (kanchan wait on 5m), plus 3 more sets + pair.
        var d = Decomp("456m789m234p234s55z", ctx);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 2 tsumo + 2 (55z pair yakuhai? 5z is haku → +2) + 2 kanchan = 26 → 30
        Assert.Equal(30, fu);
    }

    [Fact]
    public void Ryanmen_wait_no_bonus_fu()
    {
        var ctx = Tsumo("2m");
        var d = Decomp("234m456p789s234s55z", ctx);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 base + 2 tsumo + 2 (55z haku pair) = 24 → 30. No wait-fu bonus for ryanmen.
        Assert.Equal(30, fu);
    }

    [Fact]
    public void Double_wind_pair_doubles_yakuhai_fu()
    {
        // Round wind = East (27), seat wind = East (27). Pair of East = +4 fu.
        var ctx = new WinContext(Tiles.Parse("2m")[0], WinKind.Tsumo,
                                  RoundWindTileId: 27, SeatWindTileId: 27);
        var hand = Hand.FromNotation("234m456p789s234s11z");
        var d = HandDecomposer.Enumerate(hand, ctx)
            .First(x => x.Form == DecompositionForm.Standard);
        int fu = FuCalculator.Compute(d, ctx, isPinfu: false);
        // 20 + 2 tsumo + 4 (11z pair matches both winds) = 26 → 30
        Assert.Equal(30, fu);
    }
}
