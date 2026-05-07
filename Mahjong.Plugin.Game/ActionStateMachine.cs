namespace Mahjong.Plugin.Game;

/// <summary>
/// Explicit state for the auto-play tick loop. Replaces the pre-Phase-7
/// scattering of <c>actionPending</c>, <c>actionPendingStartedAt</c>,
/// <c>lastActionAt</c>, <c>lastDispatchedContext</c>, and
/// <c>riichiConfirmLatched</c> booleans with named transitions and invariants.
///
/// Invariants:
///   * <see cref="BeginDispatch"/> followed by <see cref="CompleteDispatch"/>
///     bracket every dispatch attempt — the latter MUST run in the
///     scheduling callback's <c>finally</c> so a partial failure doesn't
///     leak the in-flight flag.
///   * <see cref="TryRecoverFromStuckDispatch"/> is the one place that can
///     clear the in-flight flag without a paired <see cref="CompleteDispatch"/>.
///   * <see cref="LatchRiichiConfirm"/> is one-shot — the latch self-clears
///     when the popup goes away (via <see cref="ClearRiichiConfirm"/>) or
///     when the next tsumogiri dispatch consumes it.
///
/// Pure logic — no Dalamud, no async, no I/O. Lives in Mahjong.Plugin.Game so
/// it's covered by the same tests as the rest of the plugin contract layer.
/// </summary>
public sealed class ActionStateMachine
{
    private readonly TimeSpan dispatchTimeout;
    private readonly TimeSpan retryCooldown;

    private bool inFlight;
    private DateTime dispatchStartedAt;
    private DateTime lastActionAt;
    private DispatchContext? lastContext;
    private bool riichiConfirmLatched;

    /// <summary>
    /// Construct with the timeouts that govern stuck-dispatch recovery and
    /// per-context retry debouncing.
    /// </summary>
    /// <param name="dispatchTimeout">Force-clear the in-flight flag if this elapses with no completion.</param>
    /// <param name="retryCooldown">Don't re-dispatch the same (state, hand) until this elapses.</param>
    public ActionStateMachine(TimeSpan dispatchTimeout, TimeSpan retryCooldown)
    {
        this.dispatchTimeout = dispatchTimeout;
        this.retryCooldown = retryCooldown;
    }

    /// <summary>True if a dispatch was started and hasn't been completed (or recovered).</summary>
    public bool IsDispatchInFlight => inFlight;

    /// <summary>
    /// True iff the last successful dispatch was a riichi-accept and the
    /// game's follow-up "yaku preview" popup may still be visible. The next
    /// tick that sees the same Riichi popup completes the declaration via
    /// tsumogiri instead of re-clicking the list.
    /// </summary>
    public bool IsRiichiConfirmPending => riichiConfirmLatched;

    /// <summary>
    /// If the in-flight flag has been set longer than <see cref="dispatchTimeout"/>,
    /// clear it and return true. Defends against the case where a scheduled
    /// callback's <c>finally</c> never ran (framework shutdown, RunOnTick lost),
    /// which would otherwise stick the loop forever.
    /// </summary>
    public bool TryRecoverFromStuckDispatch(DateTime now)
    {
        if (!inFlight)
            return false;
        if (now - dispatchStartedAt <= dispatchTimeout)
            return false;
        inFlight = false;
        return true;
    }

    /// <summary>
    /// Open a dispatch window for the given context. Pairs with
    /// <see cref="CompleteDispatch"/>. Records the context so subsequent ticks
    /// can de-duplicate via <see cref="ShouldSuppressForContext"/>.
    /// </summary>
    public void BeginDispatch(DateTime now, DispatchContext context)
    {
        inFlight = true;
        dispatchStartedAt = now;
        lastActionAt = now;
        lastContext = context;
    }

    /// <summary>Close the dispatch window — call from the scheduling callback's <c>finally</c>.</summary>
    public void CompleteDispatch() => inFlight = false;

    /// <summary>
    /// True iff the given context matches the most recently dispatched and
    /// the retry cooldown hasn't elapsed yet. Callers skip the dispatch when
    /// this returns true to avoid hammering the same (state, hand) every frame.
    /// </summary>
    public bool ShouldSuppressForContext(DispatchContext context, DateTime now)
        => lastContext.HasValue
           && lastContext.Value.Equals(context)
           && now - lastActionAt < retryCooldown;

    /// <summary>Forget the most recent context — caller switched to a different mode (idle, no addon).</summary>
    public void ClearContext() => lastContext = null;

    /// <summary>Latch the riichi-confirm flag. Set after a successful auto-riichi-accept dispatch.</summary>
    public void LatchRiichiConfirm() => riichiConfirmLatched = true;

    /// <summary>Clear the latch — popup gone, hand count moved, or tsumogiri consumed it.</summary>
    public void ClearRiichiConfirm() => riichiConfirmLatched = false;
}

/// <summary>
/// Identifies a "moment" the auto-play loop is responding to — a (state code,
/// hand count) pair. Two ticks with the same context are considered the same
/// situation for retry-debounce purposes.
/// </summary>
public readonly record struct DispatchContext(int State, int Hand);
