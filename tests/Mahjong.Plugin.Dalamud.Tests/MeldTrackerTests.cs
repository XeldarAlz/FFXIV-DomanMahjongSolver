using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.Dalamud.Tests;

public class MeldTrackerTests
{
    [Fact]
    public void Starts_empty()
    {
        var tracker = new MeldTracker();
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void Record_appends_in_call_order()
    {
        var tracker = new MeldTracker();
        var pon = Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1);
        var chi = Meld.Chi(Tile.FromId(0), Tile.FromId(2), fromSeat: 3);

        tracker.Record(pon);
        tracker.Record(chi);

        Assert.Equal(2, tracker.Melds.Count);
        Assert.Equal(pon, tracker.Melds[0]);
        Assert.Equal(chi, tracker.Melds[1]);
    }

    [Fact]
    public void Clear_drops_every_meld()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.Record(Meld.AnKan(Tile.FromId(7)));

        tracker.Clear();
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ResetIfRoundEnded_clears_when_closed_hand_is_thirteen_with_open_melds()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ResetIfRoundEnded(closedHandCount: 13);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ResetIfRoundEnded_clears_when_closed_hand_is_fourteen_with_open_melds()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ResetIfRoundEnded(closedHandCount: 14);
        Assert.Empty(tracker.Melds);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(10)]
    [InlineData(8)]
    [InlineData(2)]
    [InlineData(0)]
    public void ResetIfRoundEnded_does_not_clear_below_thirteen(int closedHandCount)
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ResetIfRoundEnded(closedHandCount);
        Assert.Single(tracker.Melds);
    }

    [Fact]
    public void ResetIfRoundEnded_is_a_noop_when_already_empty()
    {
        var tracker = new MeldTracker();
        tracker.ResetIfRoundEnded(closedHandCount: 14);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void Melds_property_reflects_live_state()
    {
        var tracker = new MeldTracker();
        var snapshot1 = tracker.Melds;
        Assert.Empty(snapshot1);

        tracker.Record(Meld.AnKan(Tile.FromId(0)));
        // The tracker exposes the underlying list as IReadOnlyList — the
        // pre-write snapshot reflects subsequent writes. Pin this so a
        // future change to "return a copy" is intentional.
        Assert.Single(snapshot1);
    }
}
