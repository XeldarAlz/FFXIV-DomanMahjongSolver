using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

public class AddonPollDiscardCaptureTests
{
    private static StateSnapshot SnapshotWith(params int[][] perSeatTileIds)
    {
        var seats = new SeatView[4];
        for (int s = 0; s < 4; s++)
        {
            var ids = s < perSeatTileIds.Length ? perSeatTileIds[s] : Array.Empty<int>();
            var tiles = new Tile[ids.Length];
            for (int i = 0; i < ids.Length; i++)
                tiles[i] = Tile.FromId(ids[i]);
            seats[s] = new SeatView(
                Discards: tiles,
                DiscardIsTedashi: new bool[tiles.Length],
                Melds: Array.Empty<Meld>(),
                Riichi: false,
                RiichiDiscardIndex: -1,
                Ippatsu: false,
                IsTenpaiCalled: false);
        }
        return StateSnapshot.Empty with { Seats = seats };
    }

    [Fact]
    public void Throws_when_log_is_null()
    {
        Assert.Throws<ArgumentNullException>(() => new AddonPollDiscardCapture(null!));
    }

    [Fact]
    public void Reports_fallback_health_and_addon_poll_strategy_name()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        Assert.Equal(HookHealth.Fallback, capture.Health);
        Assert.Equal(AddonPollDiscardCapture.Name, capture.StrategyName);
        Assert.Equal("addon-poll", capture.StrategyName);
    }

    [Fact]
    public void Initial_diagnostics_are_zero()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        Assert.Equal(0UL, capture.TotalCaptured);
        Assert.Equal(-1, capture.LastTileId);
    }

    [Fact]
    public void First_snapshot_primes_counters_without_firing_events()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        // Mid-hand snapshot: each seat already has discards. The strategy
        // shouldn't replay the entire pre-existing pool.
        capture.OnSnapshotChanged(SnapshotWith(
            new[] { 5, 6, 7 },
            new[] { 10 },
            Array.Empty<int>(),
            new[] { 12, 13 }));

        Assert.Empty(observed);
        Assert.Equal(0UL, capture.TotalCaptured);
    }

    [Fact]
    public void Tile_appended_after_priming_fires_one_event()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(SnapshotWith(new[] { 5 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(SnapshotWith(new[] { 5, 9 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Single(observed);
        Assert.Equal(0, observed[0].Seat);
        Assert.Equal(9, observed[0].Tile.Id);
        Assert.Equal(1UL, observed[0].SequenceNumber);
    }

    [Fact]
    public void Multiple_seats_advance_in_one_snapshot_each_emits_in_seat_order()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(SnapshotWith(
            Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(SnapshotWith(
            new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }));

        Assert.Equal(4, observed.Count);
        Assert.Equal(0, observed[0].Seat);
        Assert.Equal(1, observed[0].Tile.Id);
        Assert.Equal(1, observed[1].Seat);
        Assert.Equal(2, observed[1].Tile.Id);
        Assert.Equal(2, observed[2].Seat);
        Assert.Equal(3, observed[2].Tile.Id);
        Assert.Equal(3, observed[3].Seat);
        Assert.Equal(4, observed[3].Tile.Id);
    }

    [Fact]
    public void Multiple_tiles_appended_to_one_seat_emit_in_order_with_monotonic_sequence()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(SnapshotWith(new[] { 5 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(SnapshotWith(new[] { 5, 6, 7 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Equal(2, observed.Count);
        Assert.Equal(6, observed[0].Tile.Id);
        Assert.Equal(7, observed[1].Tile.Id);
        Assert.Equal(1UL, observed[0].SequenceNumber);
        Assert.Equal(2UL, observed[1].SequenceNumber);
    }

    [Fact]
    public void Pool_shrinking_re_primes_without_emitting_events()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(SnapshotWith(new[] { 5, 6, 7 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        // New hand — pool dropped to one tile.
        capture.OnSnapshotChanged(SnapshotWith(new[] { 9 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Empty(observed);

        // Subsequent appends from the new baseline DO fire.
        capture.OnSnapshotChanged(SnapshotWith(new[] { 9, 10 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        Assert.Single(observed);
        Assert.Equal(10, observed[0].Tile.Id);
    }

    [Fact]
    public void TotalCaptured_and_LastTileId_track_emission()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());

        capture.OnSnapshotChanged(SnapshotWith(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(SnapshotWith(new[] { 7 }, new[] { 8 }, Array.Empty<int>(), Array.Empty<int>()));

        Assert.Equal(2UL, capture.TotalCaptured);
        Assert.Equal(8, capture.LastTileId);
    }

    [Fact]
    public void Snapshot_after_dispose_is_a_noop()
    {
        var capture = new AddonPollDiscardCapture(new StubPluginLog());
        capture.OnSnapshotChanged(SnapshotWith(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.Dispose();

        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(SnapshotWith(new[] { 1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        Assert.Empty(observed);
    }

    [Fact]
    public void Throws_on_null_snapshot()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        Assert.Throws<ArgumentNullException>(() => capture.OnSnapshotChanged(null!));
    }
}
