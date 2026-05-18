using System;
using System.Collections.Generic;
using Mahjong.Core;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// In-plugin tracker for the player's own open melds within the current round.
/// The Emj addon doesn't surface open-meld records in any memory region we've been
/// able to decode; instead the tracker infers melds from closed-hand deltas.
///
/// <para>Authoritative path: <see cref="ObserveSnapshot"/> compares the live
/// closed hand and the per-seat discard counts to their last-tick values.
/// When the closed hand shrinks by 2 or 3 tiles in lockstep with an opponent
/// seat's discard-count increment, the tracker synthesizes the chi / pon /
/// minkan that just landed and appends it to <see cref="Melds"/>.</para>
///
/// <para>The earlier FireCallback-payload heuristic (record on
/// opcode-11/option-0 + first ChiCandidate) is gone. It missed every prompt
/// shape that put the call at option 1+ (multi-variant chi, pon+chi
/// simultaneous) and every state-28 list-widget chi, and #34's
/// <c>chiClaimedTile</c> at AtkValues[19] read a wrong tile id anyway. The
/// snapshot-delta path is variant-agnostic and click-payload-agnostic — it
/// only needs the hand-array read (which works on both <c>Emj</c> and
/// <c>EmjL</c>) plus the per-seat discard count (likewise).</para>
///
/// <para>Reset between hands is via <see cref="ObserveWall"/>: a sharp upward
/// jump in wall remaining means a fresh hand has been dealt and any
/// previously-tracked melds are stale.</para>
/// </summary>
public sealed class MeldTracker
{
    /// <summary>Same ±5 read-glitch tolerance GameLogger.MaybeRollHand uses.</summary>
    private const int WallJumpThreshold = 5;

    private readonly List<Meld> melds = new();
    private readonly int[] lastDiscardCounts = new int[4];
    private int lastObservedWall = -1;
    private List<Tile>? lastHand;
    // The opp seat whose discard count incremented most recently within
    // this hand. We can't compare counts to "previous tick" when inferring
    // a meld — the opp discard fires several ticks before we click chi/pon
    // (their discard → prompt visible for seconds → our click) so a
    // last-tick comparison sees no increment at click time. Instead track
    // the most recent opp discarder, hold it through the call-prompt
    // window, and consume it when the closed-hand shrink fires.
    private int pendingOppDiscardSeat = -1;

    public IReadOnlyList<Meld> Melds => melds;

    /// <summary>
    /// Record a meld directly. Reserved for self-declared melds (AnKan,
    /// ShouMinKan) that don't show up in the closed-hand delta the same way
    /// chi/pon/minkan do. For called melds, prefer <see cref="ObserveSnapshot"/>.
    /// </summary>
    public void Record(Meld meld) => melds.Add(meld);

    /// <summary>Manual reset for commands / tests.</summary>
    public void Clear()
    {
        melds.Clear();
        lastObservedWall = -1;
        lastHand = null;
        pendingOppDiscardSeat = -1;
        Array.Clear(lastDiscardCounts);
    }

    /// <summary>
    /// Track wall remaining tick by tick. A sharp upward jump (more than
    /// <see cref="WallJumpThreshold"/>) means a fresh hand was dealt; clear
    /// every tracker state so the next snapshot starts clean.
    /// </summary>
    public void ObserveWall(int wallRemaining)
    {
        if (lastObservedWall >= 0 && wallRemaining > lastObservedWall + WallJumpThreshold)
        {
            melds.Clear();
            lastHand = null;
            pendingOppDiscardSeat = -1;
            Array.Clear(lastDiscardCounts);
        }
        lastObservedWall = wallRemaining;
    }

    /// <summary>
    /// Observe the current closed hand and per-seat discard counts. If the
    /// closed hand shrank by exactly 2 or 3 tiles since the previous
    /// observation AND an opponent seat just discarded (their count
    /// incremented), synthesize the corresponding meld and append it to
    /// <see cref="Melds"/>.
    ///
    /// <para>Returns the inferred meld, or null if nothing was inferred this
    /// tick (no prior snapshot yet, no closed-hand shrink, no opponent
    /// discard increment, or removed tiles don't form a valid run/triplet).</para>
    /// </summary>
    /// <param name="currentHand">Closed hand from the current snapshot.</param>
    /// <param name="discardCounts">Per-seat discard counts, length 4, indexed by seat.</param>
    /// <param name="ourSeat">Index of our own seat in <paramref name="discardCounts"/>.</param>
    public Meld? ObserveSnapshot(IReadOnlyList<Tile> currentHand, int[] discardCounts, int ourSeat)
    {
        ArgumentNullException.ThrowIfNull(currentHand);
        ArgumentNullException.ThrowIfNull(discardCounts);
        if (discardCounts.Length != 4)
            throw new ArgumentException("discardCounts must be length 4", nameof(discardCounts));
        if (ourSeat is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(ourSeat));

        // Latch the most-recent opp discarder. This runs every tick so that
        // when an opp discards we record the seat, hold it through the
        // call-prompt window (which can span many ticks), and have it ready
        // when the closed-hand shrink fires on our chi/pon click.
        UpdatePendingOppDiscarder(discardCounts, ourSeat);

        Meld? inferred = null;
        if (lastHand is not null && pendingOppDiscardSeat >= 0)
        {
            int delta = lastHand.Count - currentHand.Count;
            if (delta is 2 or 3)
            {
                var removed = DiffRemoved(lastHand, currentHand);
                if (removed.Count == delta)
                {
                    inferred = delta == 2
                        ? InferChiOrPon(removed, pendingOppDiscardSeat)
                        : InferMinKan(removed, pendingOppDiscardSeat);
                    if (inferred is { } m)
                    {
                        melds.Add(m);
                        // The pending discarder is consumed by this call —
                        // a subsequent chi/pon needs its own fresh opp
                        // discard to fire.
                        pendingOppDiscardSeat = -1;
                    }
                }
            }
        }

        // Snapshot state for next tick. Copy currentHand so later mutations
        // by the caller (e.g. snapshot reuse) don't invalidate our diff.
        lastHand = new List<Tile>(currentHand);
        Array.Copy(discardCounts, lastDiscardCounts, 4);
        return inferred;
    }

    private void UpdatePendingOppDiscarder(int[] discardCounts, int ourSeat)
    {
        for (int i = 0; i < 4; i++)
        {
            if (i == ourSeat)
                continue;
            if (discardCounts[i] > lastDiscardCounts[i])
                pendingOppDiscardSeat = i;
        }
    }

    private static List<Tile> DiffRemoved(IReadOnlyList<Tile> before, IReadOnlyList<Tile> after)
    {
        var counts = new int[Tile.Count34];
        foreach (var t in before)
            counts[t.Id]++;
        foreach (var t in after)
            counts[t.Id]--;

        var removed = new List<Tile>();
        for (int id = 0; id < Tile.Count34; id++)
        {
            int c = counts[id];
            if (c <= 0)
                continue;
            for (int i = 0; i < c; i++)
                removed.Add(new Tile((byte)id));
        }
        return removed;
    }

    private static Meld? InferChiOrPon(List<Tile> removed, int fromSeat)
    {
        var a = removed[0];
        var b = removed[1];

        if (a.Id == b.Id)
            return Meld.Pon(a, a, fromSeat);

        // Chi requires same suit, suited (no honors), consecutive ids.
        if (a.Suit == TileSuit.Honor || a.Suit != b.Suit)
            return null;
        int diff = b.Id - a.Id;
        if (diff is not (1 or 2))
            return null;

        // Resolve the run's low tile.
        //   diff=2 (e.g. 4m,6m): called tile is the middle. Run low = a.
        //   diff=1 (e.g. 4m,5m): called tile is either a-1 or b+1. Prefer
        //     down-extension when in-suit, otherwise up-extension. Either
        //     produces a valid Chi; we can't disambiguate without seeing
        //     the opponent's discard pool (Track D-2).
        Tile low;
        if (diff == 2)
        {
            low = a;
        }
        else
        {
            var down = new Tile((byte)(a.Id - 1));
            low = (a.Id > 0 && down.Suit == a.Suit) ? down : a;
        }

        Tile called = FindCalledTile(low, a, b);
        return Meld.Chi(low, called, fromSeat);
    }

    private static Tile FindCalledTile(Tile low, Tile a, Tile b)
    {
        // The called tile is whichever of low, low+1, low+2 we don't already hold.
        var t0 = low;
        var t1 = new Tile((byte)(low.Id + 1));
        var t2 = new Tile((byte)(low.Id + 2));
        if (t0.Id != a.Id && t0.Id != b.Id)
            return t0;
        if (t1.Id != a.Id && t1.Id != b.Id)
            return t1;
        return t2;
    }

    private static Meld? InferMinKan(List<Tile> removed, int fromSeat)
    {
        // MinKan: all three removed tiles are the same id (the called fourth
        // arrived from the opponent's discard).
        if (removed[0].Id == removed[1].Id && removed[1].Id == removed[2].Id)
            return Meld.MinKan(removed[0], removed[0], fromSeat);
        return null;
    }
}
