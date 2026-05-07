using System;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks.Strategies;

/// <summary>
/// Fallback <see cref="IDiscardCapture"/> strategy. Activates when the native
/// asm hook can't (typically a patched binary the sigscan doesn't grok).
///
/// <para>Diffs each seat's discard list against the previous snapshot every
/// time <see cref="OnSnapshotChanged"/> is invoked — typically wired up to
/// <c>StateAggregator.Changed</c> in <see cref="Plugin"/>. Every newly-appended
/// tile fires a <see cref="DiscardObserved"/> event with the seat attached —
/// strictly more information than the native strategy provides.</para>
///
/// <para>Trade-off vs. native: events fire one snapshot tick (~16ms) later
/// than the actual game write, and a dropped snapshot can paper over a
/// transient discard. For opponent-model bookkeeping that's acceptable —
/// the policy pipeline already consumes <see cref="StateSnapshot"/> with the
/// same latency profile.</para>
///
/// <para>The strategy doesn't subscribe to anything itself — the wiring lives
/// at the composition root. That seam keeps the class testable without
/// having to fabricate a real StateAggregator (and its IFramework /
/// AddonEmjReader dependencies).</para>
/// </summary>
public sealed class AddonPollDiscardCapture : IDiscardCapture
{
    public const string Name = "addon-poll";

    private readonly int[] lastDiscardCounts = new int[4];
    private bool primed;
    private ulong totalCaptured;
    private int lastTileId = -1;
    private bool disposed;

    public HookHealth Health { get; } = HookHealth.Fallback;
    public string StrategyName => Name;
    public ulong TotalCaptured => totalCaptured;
    public int LastTileId => lastTileId;
    public event Action<DiscardEvent>? DiscardObserved;

    public AddonPollDiscardCapture(IPluginLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.Info(
            "[DiscardCapture/addon-poll] active — inferring discards from " +
            "StateAggregator snapshots (native asm hook unavailable).");
    }

    /// <summary>
    /// Diff the prior per-seat discard counts against the new snapshot. The
    /// first snapshot primes the counters — we don't want to flood the event
    /// with the entire pre-existing pool when the plugin loads mid-hand.
    /// </summary>
    public void OnSnapshotChanged(StateSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (disposed)
            return;

        var seats = snap.Seats;
        if (!primed)
        {
            for (int i = 0; i < 4 && i < seats.Count; i++)
                lastDiscardCounts[i] = seats[i].Discards.Count;
            primed = true;
            return;
        }

        var now = DateTime.UtcNow;
        for (int seat = 0; seat < 4 && seat < seats.Count; seat++)
        {
            var discards = seats[seat].Discards;
            int prev = lastDiscardCounts[seat];
            int curr = discards.Count;

            // New hand — pool got smaller. Re-prime, don't fire events.
            if (curr < prev)
            {
                lastDiscardCounts[seat] = curr;
                continue;
            }
            for (int i = prev; i < curr; i++)
                EmitDiscard(seat, discards[i], now);
            lastDiscardCounts[seat] = curr;
        }
    }

    private void EmitDiscard(int seat, Tile tile, DateTime now)
    {
        totalCaptured++;
        lastTileId = tile.Id;
        DiscardObserved?.Invoke(new DiscardEvent(
            Seat: seat,
            Tile: tile,
            ObservedAtUtc: now,
            SequenceNumber: totalCaptured));
    }

    public void Dispose()
    {
        disposed = true;
    }
}
