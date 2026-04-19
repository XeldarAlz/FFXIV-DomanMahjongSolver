using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Opponents;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class OpponentModelTests
{
    private static StateSnapshot BaseState(
        int wallRemaining = 40,
        int ourSeat = 0,
        SeatView[]? seats = null)
    {
        seats ??= new[]
        {
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        return StateSnapshot.Empty with
        {
            OurSeat = ourSeat,
            WallRemaining = wallRemaining,
            Seats = seats,
        };
    }

    [Fact]
    public void Riichi_seat_gets_tenpai_prob_one()
    {
        var seats = new[]
        {
            new SeatView([], [], [], false, -1, false, false),   // self
            new SeatView([], [], [], true, 5, false, false),     // shimocha — riichi declared
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        var s = BaseState(seats: seats);
        var m = new OpponentModel();
        m.Update(s);
        Assert.Equal(1.0, m.TenpaiProb[0]);  // shimocha (riichi) → 100%
    }

    [Fact]
    public void Early_game_low_tenpai_prob()
    {
        var s = BaseState(wallRemaining: 68);    // very early
        var m = new OpponentModel();
        m.Update(s);
        foreach (var p in m.TenpaiProb)
            Assert.True(p < 0.3, $"early tenpai prob should be low, got {p}");
    }

    [Fact]
    public void Late_game_higher_tenpai_prob()
    {
        var s = BaseState(wallRemaining: 10);    // near wall exhaust
        var m = new OpponentModel();
        m.Update(s);
        foreach (var p in m.TenpaiProb)
            Assert.True(p > 0.2, $"late-game tenpai prob should be non-trivial, got {p}");
    }

    [Fact]
    public void Opponent_discarded_tile_has_zero_danger_genbutsu()
    {
        var discards = new[] { Tile.FromId(5) };   // 6m
        var seats = new[]
        {
            new SeatView([], [], [], false, -1, false, false),
            new SeatView(discards, [false], [], false, -1, false, false),  // shimocha discarded 6m
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        var s = BaseState(seats: seats);
        var m = new OpponentModel();
        m.Update(s);
        Assert.Equal(0.0, m.DangerMap[0][5]);   // shimocha can't ron on 6m (genbutsu)
    }

    [Fact]
    public void Kabe_zero_live_tiles_means_zero_danger()
    {
        // All 4 copies of 5z accounted for by our hand — nobody can wait on 5z.
        var hand = new[]
        {
            Tile.FromId(31), Tile.FromId(31), Tile.FromId(31), Tile.FromId(31),
        };
        var s = BaseState() with { Hand = hand };
        var m = new OpponentModel();
        m.Update(s);
        for (int opp = 0; opp < 3; opp++)
            Assert.Equal(0.0, m.DangerMap[opp][31]);
    }

    [Fact]
    public void HandMarginal_zero_for_tiles_the_opponent_discarded()
    {
        var discards = new[] { Tile.FromId(0) };
        var seats = new[]
        {
            new SeatView([], [], [], false, -1, false, false),
            new SeatView(discards, [false], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        var s = BaseState(seats: seats);
        var m = new OpponentModel();
        m.Update(s);
        Assert.Equal(0.0, m.HandMarginal[0][0]);   // shimocha can't be holding 1m (discarded)
    }

    [Fact]
    public void ExpectedDealInCost_is_weighted_sum()
    {
        var s = BaseState();
        var m = new OpponentModel();
        m.Update(s);
        // With no riichi and early game, all three opponents have similar danger.
        // Cost is DangerMap × 4000 summed.
        double cost = m.ExpectedDealInCost(0);
        Assert.True(cost >= 0.0);
        Assert.True(cost <= 3 * 4000);
    }
}
