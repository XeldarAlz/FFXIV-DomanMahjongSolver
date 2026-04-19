namespace DomanMahjongAI.GameState;

/// <summary>
/// Raw observation of the AddonEmj's current state, captured by
/// <see cref="AddonEmjReader"/> on each lifecycle event or framework tick.
/// Purely informational — used by the debug overlay and diagnostics, not
/// by the decision pipeline (that consumes <c>StateSnapshot</c> instead).
/// </summary>
public sealed record AddonEmjObservation(
    bool Present,
    bool IsVisible,
    nint Address,
    ushort Width,
    ushort Height,
    long LastSeenUtcTicks,
    string? LastLifecycleEvent)
{
    public static AddonEmjObservation Empty { get; } =
        new(false, false, 0, 0, 0, 0, null);
}
