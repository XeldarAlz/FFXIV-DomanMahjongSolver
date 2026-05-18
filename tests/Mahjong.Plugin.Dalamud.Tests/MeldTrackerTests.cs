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
    public void ObserveWall_clears_on_wall_jump_up()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ObserveWall(20);
        tracker.ObserveWall(70);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ObserveWall_does_not_clear_within_a_hand()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ObserveWall(70);
        tracker.ObserveWall(60);
        tracker.ObserveWall(40);
        tracker.ObserveWall(10);
        Assert.Single(tracker.Melds);
    }

    [Fact]
    public void ObserveWall_tolerates_minor_jitter()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(5), Tile.FromId(5), fromSeat: 1));
        tracker.ObserveWall(20);
        // ±5 read-glitch tolerance — same threshold GameLogger.MaybeRollHand uses.
        tracker.ObserveWall(24);
        tracker.ObserveWall(22);
        Assert.Single(tracker.Melds);
    }

    [Fact]
    public void ObserveWall_is_a_noop_when_already_empty()
    {
        var tracker = new MeldTracker();
        tracker.ObserveWall(20);
        tracker.ObserveWall(70);
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

    // ---------------- ObserveSnapshot inference ----------------

    private static Tile[] Hand(string s) => Tiles.Parse(s);

    [Fact]
    public void ObserveSnapshot_first_call_returns_null()
    {
        // First observation establishes a baseline — no prior to diff against.
        var tracker = new MeldTracker();
        var inferred = tracker.ObserveSnapshot(Hand("123m45p67s11z"), [0, 0, 0, 0], ourSeat: 0);
        Assert.Null(inferred);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ObserveSnapshot_infers_pon_when_pair_disappears_and_opp_discarded()
    {
        var tracker = new MeldTracker();
        // 13 tiles, holds pair of 5m
        tracker.ObserveSnapshot(Hand("123m55m789p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        // 11 tiles, both 5m gone; seat 2 just discarded.
        var inferred = tracker.ObserveSnapshot(Hand("123m789p1234567z"), [0, 0, 1, 0], ourSeat: 0);

        Assert.NotNull(inferred);
        Assert.Equal(MeldKind.Pon, inferred!.Value.Kind);
        Assert.Equal(4, inferred.Value.ClaimedTile!.Value.Id); // 5m = id 4
        Assert.Equal(2, inferred.Value.ClaimedFromSeat);
        Assert.Single(tracker.Melds);
    }

    [Fact]
    public void ObserveSnapshot_infers_chi_with_adjacent_pair_low_extension()
    {
        var tracker = new MeldTracker();
        // 13 tiles incl. 4m,5m; chi the left neighbor's 3m to form 345m.
        tracker.ObserveSnapshot(Hand("245m12345p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        // 11 tiles, 4m+5m removed; left neighbor (seat 3 — toimen-1 in 4-seat) discarded.
        var inferred = tracker.ObserveSnapshot(Hand("2m12345p1234567z"), [0, 0, 0, 1], ourSeat: 0);

        Assert.NotNull(inferred);
        Assert.Equal(MeldKind.Chi, inferred!.Value.Kind);
        // diff=1 with 4m,5m: prefer down-extension → run is 3m,4m,5m, low=3m (id 2).
        Assert.Equal(2, inferred.Value.Tiles[0].Id);
        Assert.Equal(3, inferred.Value.Tiles[1].Id);
        Assert.Equal(4, inferred.Value.Tiles[2].Id);
        Assert.Equal(2, inferred.Value.ClaimedTile!.Value.Id);
        Assert.Equal(3, inferred.Value.ClaimedFromSeat);
    }

    [Fact]
    public void ObserveSnapshot_infers_chi_with_gapped_pair_middle_call()
    {
        var tracker = new MeldTracker();
        // 13 tiles incl. 4m,6m; chi the missing 5m middle to form 456m.
        tracker.ObserveSnapshot(Hand("246m12345p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        var inferred = tracker.ObserveSnapshot(Hand("2m12345p1234567z"), [0, 0, 0, 1], ourSeat: 0);

        Assert.NotNull(inferred);
        Assert.Equal(MeldKind.Chi, inferred!.Value.Kind);
        // Run = 4m,5m,6m → low = 4m (id 3), called = 5m (id 4).
        Assert.Equal(3, inferred.Value.Tiles[0].Id);
        Assert.Equal(4, inferred.Value.ClaimedTile!.Value.Id);
    }

    [Fact]
    public void ObserveSnapshot_chi_respects_suit_boundary_at_1m_2m()
    {
        var tracker = new MeldTracker();
        // 13 tiles incl. 1m,2m; chi must extend up (1-2-3m), not down across to 9m of prior suit.
        tracker.ObserveSnapshot(Hand("12m1234p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        var inferred = tracker.ObserveSnapshot(Hand("1234p1234567z"), [0, 0, 0, 1], ourSeat: 0);

        Assert.NotNull(inferred);
        Assert.Equal(MeldKind.Chi, inferred!.Value.Kind);
        // Down-extension would underflow id 0; tracker must pick up-extension:
        // low = 1m (id 0), run 1m,2m,3m, called = 3m.
        Assert.Equal(0, inferred.Value.Tiles[0].Id);
        Assert.Equal(2, inferred.Value.ClaimedTile!.Value.Id);
    }

    [Fact]
    public void ObserveSnapshot_infers_minkan_when_triplet_disappears_and_opp_discarded()
    {
        var tracker = new MeldTracker();
        // 14 tiles incl. triplet of 7p (id 15), call minkan on 4th from opp.
        tracker.ObserveSnapshot(Hand("123m777p123s1234z"), [0, 0, 0, 0], ourSeat: 0);
        var inferred = tracker.ObserveSnapshot(Hand("123m123s1234z"), [0, 1, 0, 0], ourSeat: 0);

        Assert.NotNull(inferred);
        Assert.Equal(MeldKind.MinKan, inferred!.Value.Kind);
        Assert.Equal(15, inferred.Value.ClaimedTile!.Value.Id);
        Assert.Equal(1, inferred.Value.ClaimedFromSeat);
        Assert.Equal(4, inferred.Value.TileCount);
    }

    [Fact]
    public void ObserveSnapshot_returns_null_when_only_we_discarded()
    {
        var tracker = new MeldTracker();
        tracker.ObserveSnapshot(Hand("123m45p67s11z123m"), [0, 0, 0, 0], ourSeat: 0);
        // Hand shrank by 1 (a normal discard) — not a meld.
        var inferred = tracker.ObserveSnapshot(Hand("123m45p67s11z23m"), [1, 0, 0, 0], ourSeat: 0);
        Assert.Null(inferred);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ObserveSnapshot_returns_null_when_hand_shrunk_but_no_opp_discarded()
    {
        // Unlikely physical case (ankan would self-declare, no opp delta), but
        // the guard still has to hold: never infer a called meld without an
        // opponent discard signal.
        var tracker = new MeldTracker();
        tracker.ObserveSnapshot(Hand("123m55m789p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        var inferred = tracker.ObserveSnapshot(Hand("123m789p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        Assert.Null(inferred);
    }

    [Fact]
    public void ObserveSnapshot_returns_null_when_removed_tiles_form_invalid_chi()
    {
        var tracker = new MeldTracker();
        // Two non-consecutive, non-pair tiles disappear — can't form a real meld.
        tracker.ObserveSnapshot(Hand("13m45p67s11z123m9p"), [0, 0, 0, 0], ourSeat: 0);
        var inferred = tracker.ObserveSnapshot(Hand("45p67s11z123m"), [0, 0, 1, 0], ourSeat: 0);
        Assert.Null(inferred);
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void ObserveSnapshot_does_not_double_record_after_wall_reset()
    {
        // Hand boundary clears state — a chi inferred in hand 1 should not
        // re-fire on the first observation of hand 2.
        var tracker = new MeldTracker();
        tracker.ObserveWall(40);
        tracker.ObserveSnapshot(Hand("245m12345p1234567z"), [0, 0, 0, 0], ourSeat: 0);
        tracker.ObserveSnapshot(Hand("2m12345p1234567z"), [0, 0, 0, 1], ourSeat: 0);
        Assert.Single(tracker.Melds);

        // New hand begins — wall jumps from 40 back to 70.
        tracker.ObserveWall(70);
        Assert.Empty(tracker.Melds);

        // First observation of new hand — must not synthesize anything from
        // stale prior-hand state.
        var inferred = tracker.ObserveSnapshot(Hand("123m45p67s11z123m"), [0, 0, 0, 0], ourSeat: 0);
        Assert.Null(inferred);
    }

    [Fact]
    public void ObserveSnapshot_respects_ourSeat_when_classifying_discard_owner()
    {
        // If our own discard-count incremented, that's our discard (not a call
        // we made). Even with a hand shrink of 2, no meld should be inferred
        // unless an OPPONENT's count moved.
        var tracker = new MeldTracker();
        tracker.ObserveSnapshot(Hand("123m55m789p1234567z"), [0, 0, 0, 0], ourSeat: 2);
        var inferred = tracker.ObserveSnapshot(Hand("123m789p1234567z"), [0, 0, 1, 0], ourSeat: 2);
        Assert.Null(inferred);
    }
}
