using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Efficiency;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class RiichiEvaluatorTests
{
    private static readonly Tile Anchor2m = Tiles.Parse("2m")[0];

    private static StateSnapshot BaseState(int wallRemaining = 40, int[]? scores = null)
    {
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++) seats[i] = new SeatView([], [], [], false, -1, false, false);
        return StateSnapshot.Empty with
        {
            Scores = scores ?? [25000, 25000, 25000, 25000],
            WallRemaining = wallRemaining,
            Seats = seats,
        };
    }

    [Fact]
    public void Declares_when_all_conditions_met()
    {
        var s = BaseState();
        var r = RiichiEvaluator.Evaluate(s, Anchor2m,
            weightedUkeireAfterDiscard: 8, acceptedKindsAfterDiscard: 2, shantenAfterDiscard: 0);
        Assert.True(r.Declare, r.Reason);
    }

    [Fact]
    public void Rejects_when_not_tenpai()
    {
        var s = BaseState();
        var r = RiichiEvaluator.Evaluate(s, Anchor2m, 8, 2, shantenAfterDiscard: 1);
        Assert.False(r.Declare);
        Assert.Contains("not tenpai", r.Reason);
    }

    [Fact]
    public void Rejects_when_score_too_low()
    {
        var s = BaseState(scores: [500, 30000, 30000, 39500]);   // our seat 0 has 500 (< 1000)
        var r = RiichiEvaluator.Evaluate(s, Anchor2m, 8, 2, shantenAfterDiscard: 0);
        Assert.False(r.Declare);
        Assert.Contains("< 1000", r.Reason);
    }

    [Fact]
    public void Rejects_when_wall_nearly_empty()
    {
        var s = BaseState(wallRemaining: 3);
        var r = RiichiEvaluator.Evaluate(s, Anchor2m, 8, 2, shantenAfterDiscard: 0);
        Assert.False(r.Declare);
        Assert.Contains("wall", r.Reason);
    }

    [Fact]
    public void Rejects_when_ukeire_too_thin()
    {
        var s = BaseState();
        var r = RiichiEvaluator.Evaluate(s, Anchor2m,
            weightedUkeireAfterDiscard: 2, acceptedKindsAfterDiscard: 1, shantenAfterDiscard: 0);
        Assert.False(r.Declare);
        Assert.Contains("live accepting tiles", r.Reason);
    }

    [Fact]
    public void Rejects_when_hand_is_open()
    {
        var pon = Meld.Pon(Tile.FromId(0), Tile.FromId(0), fromSeat: 2);
        var s = BaseState() with { OurMelds = [pon] };
        var r = RiichiEvaluator.Evaluate(s, Anchor2m, 8, 2, 0);
        Assert.False(r.Declare);
        Assert.Contains("open", r.Reason);
    }

    [Fact]
    public void Ankan_does_not_count_as_open()
    {
        var ankan = Meld.AnKan(Tile.FromId(0));
        var s = BaseState() with { OurMelds = [ankan] };
        var r = RiichiEvaluator.Evaluate(s, Anchor2m, 8, 2, 0);
        Assert.True(r.Declare);
    }
}
