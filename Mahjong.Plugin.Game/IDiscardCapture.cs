using Mahjong.Core;

namespace Mahjong.Plugin.Game;

public enum HookHealth
{
    /// <summary>Native hook installed and live.</summary>
    Active,

    /// <summary>Native hook failed to install; fallback strategy in use.</summary>
    Fallback,

    /// <summary>Capture is offline — neither hook nor fallback is operational.</summary>
    Offline,
}

/// <summary>
/// Observes Doman mahjong discards the moment they commit, regardless of how
/// the implementation observes them. The plugin's primary path is a native
/// asm hook on the discard-write instruction; the fallback infers discards
/// from <c>StateSnapshot</c> diffs via the addon callback. Both reduce to
/// the same contract: a push event that fires once per captured discard.
///
/// <para>Subscribers run on the framework thread — implementations marshal
/// to it before firing.</para>
/// </summary>
public interface IDiscardCapture : IDisposable
{
    /// <summary>
    /// Strategy state. <see cref="HookHealth.Active"/> means the native asm
    /// hook is live; <see cref="HookHealth.Fallback"/> means the addon-poll
    /// path is in use; <see cref="HookHealth.Offline"/> means neither could
    /// activate (rare — usually a patched binary the plugin doesn't grok).
    /// </summary>
    HookHealth Health { get; }

    /// <summary>
    /// Short identifier of the underlying strategy (<c>"native-asm"</c>,
    /// <c>"addon-poll"</c>, <c>"inert"</c>). Diagnostic only.
    /// </summary>
    string StrategyName { get; }

    /// <summary>Total discards captured since construction. Diagnostic.</summary>
    ulong TotalCaptured { get; }

    /// <summary>
    /// Tile id of the most recently captured discard, or <c>-1</c> if none
    /// yet. Diagnostic — actual events flow through <see cref="DiscardObserved"/>.
    /// </summary>
    int LastTileId { get; }

    /// <summary>Fired on each observed discard. Subscribers run on the framework thread.</summary>
    event Action<DiscardEvent>? DiscardObserved;
}

/// <summary>
/// One observed opponent discard.
/// </summary>
/// <param name="Seat">
/// Seat index 0..3 if the strategy can attribute the discard, else <c>-1</c>.
/// The native asm strategy reports -1 because it sees a pool address rather
/// than a seat index.
/// </param>
/// <param name="Tile">The discarded tile (kind-level, 0..33).</param>
/// <param name="ObservedAtUtc">UTC timestamp the strategy noticed the discard.</param>
/// <param name="SequenceNumber">
/// Monotonic counter assigned at capture. Useful for deduping when consumers
/// reconcile push events with snapshot-derived state.
/// </param>
public readonly record struct DiscardEvent(
    int Seat, Tile Tile, DateTime ObservedAtUtc, ulong SequenceNumber);
