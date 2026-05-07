using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Tests;

/// <summary>
/// Edge-case suite extending <see cref="AddonPollDiscardCaptureTests"/>.
/// </summary>
public class AddonPollDiscardCaptureExtraTests
{
    private static StateSnapshot Snap(int[] s0, int[] s1, int[] s2, int[] s3)
    {
        var seats = new SeatView[4];
        var raws = new[] { s0, s1, s2, s3 };
        for (int s = 0; s < 4; s++)
        {
            var ids = raws[s];
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
    public void Empty_first_snapshot_primes_to_all_zeros()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(Snap(new[] { 1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Single(observed);
    }

    [Fact]
    public void ObservedAtUtc_is_in_utc()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(Snap(new[] { 1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Equal(DateTimeKind.Utc, observed[0].ObservedAtUtc.Kind);
    }

    [Fact]
    public void Sequence_numbers_are_globally_monotonic_across_seats()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.OnSnapshotChanged(Snap(new[] { 1 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>()));
        capture.OnSnapshotChanged(Snap(new[] { 1, 4 }, new[] { 2 }, new[] { 3 }, Array.Empty<int>()));

        // Sequence: seat0 first (1), seat1 (2), seat2 (3), seat0 again (4).
        Assert.Equal(4, observed.Count);
        for (int i = 0; i < observed.Count; i++)
            Assert.Equal((ulong)(i + 1), observed[i].SequenceNumber);
    }

    [Fact]
    public void Each_seats_pool_is_tracked_independently()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(new[] { 1 }, new[] { 2 }, new[] { 3 }, new[] { 4 }));
        // Only seat 2 advances.
        capture.OnSnapshotChanged(Snap(new[] { 1 }, new[] { 2 }, new[] { 3, 5 }, new[] { 4 }));

        Assert.Single(observed);
        Assert.Equal(2, observed[0].Seat);
        Assert.Equal(5, observed[0].Tile.Id);
    }

    [Fact]
    public void Re_priming_after_a_shrunk_pool_does_not_emit_new_events_in_same_snapshot()
    {
        using var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(new[] { 5, 6, 7 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        // Round restart — pool shrinks to 2 tiles. The strategy should
        // re-prime to 2 silently without emitting either of them.
        capture.OnSnapshotChanged(Snap(new[] { 9, 10 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));

        Assert.Empty(observed);
    }

    [Fact]
    public void Disposed_capture_stops_emitting()
    {
        var capture = new AddonPollDiscardCapture(new StubPluginLog());
        var observed = new List<DiscardEvent>();
        capture.DiscardObserved += observed.Add;

        capture.OnSnapshotChanged(Snap(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        capture.Dispose();

        capture.OnSnapshotChanged(Snap(new[] { 1, 2, 3 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        Assert.Empty(observed);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var capture = new AddonPollDiscardCapture(new StubPluginLog());
        capture.Dispose();
        capture.Dispose();
    }
}
