using DomanMahjongAI.Engine;
using DomanMahjongAI.Policy.Mcts;
using DomanMahjongAI.Policy.Opponents;
using Xunit;

namespace DomanMahjongAI.Policy.Tests;

public class DeterminizerTests
{
    private static StateSnapshot BaseState()
    {
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView([], [], [], false, -1, false, false);
        return StateSnapshot.Empty with { Seats = seats };
    }

    [Fact]
    public void Sample_returns_three_opponent_hands_of_13()
    {
        var s = BaseState() with { Hand = Tiles.Parse("123m456p789s234s55z").AsReadOnly().ToArray() };
        var m = new OpponentModel();
        m.Update(s);
        var d = new Determinizer(seed: 42);
        var sample = d.Sample(s, m);
        Assert.NotNull(sample);
        Assert.Equal(3, sample!.Value.OpponentHands.Length);
        foreach (var h in sample.Value.OpponentHands)
            Assert.Equal(13, h.Length);
    }

    [Fact]
    public void Sample_excludes_tiles_in_our_hand()
    {
        // Our hand has four 1m tiles. Opponents cannot hold any 1m.
        var s = BaseState() with { Hand = new[] {
            Tile.FromId(0), Tile.FromId(0), Tile.FromId(0), Tile.FromId(0),
            Tile.FromId(1), Tile.FromId(2), Tile.FromId(3), Tile.FromId(4),
            Tile.FromId(5), Tile.FromId(6), Tile.FromId(7), Tile.FromId(8),
            Tile.FromId(9), Tile.FromId(10),
        } };
        var m = new OpponentModel();
        m.Update(s);
        var d = new Determinizer(seed: 123);
        var sample = d.Sample(s, m);
        Assert.NotNull(sample);
        foreach (var hand in sample!.Value.OpponentHands)
            foreach (var t in hand)
                Assert.NotEqual(0, t.Id);
    }

    [Fact]
    public void Opponent_with_open_meld_gets_smaller_hand()
    {
        var ponOfFourMan = Meld.Pon(Tile.FromId(3), Tile.FromId(3), 2);
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView([], [], [], false, -1, false, false);
        // Shimocha (seat index 1 relative to us as seat 0) has 1 open meld → 10 closed tiles
        seats[1] = seats[1] with { Melds = new[] { ponOfFourMan } };

        var s = StateSnapshot.Empty with { Seats = seats };
        var m = new OpponentModel();
        m.Update(s);
        var d = new Determinizer(seed: 7);
        var sample = d.Sample(s, m);
        Assert.NotNull(sample);
        Assert.Equal(10, sample!.Value.OpponentHands[0].Length);   // shimocha
        Assert.Equal(13, sample.Value.OpponentHands[1].Length);    // toimen
        Assert.Equal(13, sample.Value.OpponentHands[2].Length);    // kamicha
    }

    [Fact]
    public void Multiple_samples_produce_different_determinizations()
    {
        var s = BaseState();
        var m = new OpponentModel();
        m.Update(s);
        var d = new Determinizer(seed: 0);
        var s1 = d.Sample(s, m);
        var s2 = d.Sample(s, m);
        Assert.NotNull(s1);
        Assert.NotNull(s2);
        // Statistically essentially-certain that 39 tiles shuffle identically only once.
        bool allSame = true;
        for (int opp = 0; opp < 3 && allSame; opp++)
        {
            for (int i = 0; i < s1!.Value.OpponentHands[opp].Length; i++)
                if (s1.Value.OpponentHands[opp][i].Id != s2!.Value.OpponentHands[opp][i].Id)
                    { allSame = false; break; }
        }
        Assert.False(allSame);
    }
}
