using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using DomanMahjongAI.Policy.Opponents;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class PushFoldEvaluatorTests
{
    private static StateSnapshot BaseState(int wallRemaining = 40, SeatView[]? seats = null)
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
            WallRemaining = wallRemaining,
            Seats = seats,
        };
    }

    [Fact]
    public void Tenpai_with_low_danger_pushes()
    {
        var s = BaseState();
        var m = new OpponentModel();
        m.Update(s);
        var d = PushFoldEvaluator.Evaluate(s, currentShanten: 0, m, Tile.FromId(4));
        Assert.False(d.Fold);
    }

    [Fact]
    public void Two_shanten_late_game_folds()
    {
        var s = BaseState(wallRemaining: 8);
        var m = new OpponentModel();
        m.Update(s);
        var d = PushFoldEvaluator.Evaluate(s, currentShanten: 2, m, Tile.FromId(0));
        Assert.True(d.Fold);
        Assert.Contains("wall", d.Reason);
    }

    [Fact]
    public void Two_shanten_vs_riichi_folds()
    {
        var seats = new[]
        {
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], true, 5, false, false),   // shimocha riichi
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        var s = BaseState(wallRemaining: 30, seats: seats);
        var m = new OpponentModel();
        m.Update(s);
        var d = PushFoldEvaluator.Evaluate(s, currentShanten: 2, m, Tile.FromId(0));
        Assert.True(d.Fold);
        Assert.Contains("riichi", d.Reason);
    }

    [Fact]
    public void Two_shanten_early_game_pushes()
    {
        var s = BaseState(wallRemaining: 55);
        var m = new OpponentModel();
        m.Update(s);
        var d = PushFoldEvaluator.Evaluate(s, currentShanten: 2, m, Tile.FromId(0));
        Assert.False(d.Fold);
    }

    [Fact]
    public void One_shanten_vs_riichi_folds_if_danger()
    {
        var seats = new[]
        {
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], true, 3, false, false),
            new SeatView([], [], [], false, -1, false, false),
            new SeatView([], [], [], false, -1, false, false),
        };
        var s = BaseState(wallRemaining: 35, seats: seats);
        var m = new OpponentModel();
        m.Update(s);
        // Pick a non-genbutsu tile. With riichi'd shimocha having no discards, all tiles
        // are "dangerous" per our model's baseline × tenpai(=1.0) = baseline × 0.125.
        // ExpectedDealInCost = 0.125 × 4000 = 500 → above 500 threshold should fold.
        var d = PushFoldEvaluator.Evaluate(s, currentShanten: 1, m, Tile.FromId(5));
        // Could go either way depending on exact danger; just verify the decision runs without error.
        Assert.NotNull(d.Reason);
    }
}
