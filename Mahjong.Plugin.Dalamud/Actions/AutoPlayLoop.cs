using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Policy;

namespace Mahjong.Plugin.Dalamud.Actions;

/// <summary>
/// Continuous auto-play loop. Drives the Emj addon through its state machine via
/// <see cref="InputDispatcher"/>:
/// <list type="bullet">
///   <item>Discard turn (Legal.Can(Discard)) → policy picks → discard/riichi</item>
///   <item>Call prompt (pon/chi/kan/ron/riichi/tsumo modal visible) → policy picks → accept or pass</item>
///   <item>State 25 (chi-variant selection, the follow-up after accepting chi with
///       multiple possible sequences) → dispatch opt=0 to pick the first variant</item>
/// </list>
/// All other states (opponent turn, animations, hand-end) are ignored.
///
/// Gated by configuration: requires <c>AutomationArmed</c> true,
/// <c>SuggestionOnly</c> false, and <c>TosAccepted</c> true.
///
/// State management lives in <see cref="ActionStateMachine"/> — every
/// in-flight flag, retry-debounce timestamp, and the riichi-confirm latch are
/// transitions on that explicit FSM rather than ad-hoc booleans.
/// </summary>
public sealed class AutoPlayLoop : IDisposable
{
    private const int ChiVariantSelectStateCode = 25;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10.0);

    private const int VariantAcceptDelayMs = 500;
    private const int CallDecisionDelayMs = 700;
    private const int RiichiTsumogiriDelayMs = 700;

    private const ActionFlags CallPromptFlags =
        ActionFlags.Pon | ActionFlags.Chi |
        ActionFlags.MinKan | ActionFlags.ShouMinKan |
        ActionFlags.Ron | ActionFlags.Riichi | ActionFlags.Tsumo;

    private readonly Plugin plugin;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly MahjongAddon addon;
    private readonly ActionStateMachine fsm = new(DispatchTimeout, RetryCooldown);
    private bool disposed;

    /// <summary>Short human-readable description of the most recent auto action. For the overlay.</summary>
    public string LastActionDescription { get; private set; } = "(none)";

    /// <summary>State code snapshot from the last tick. For the overlay.</summary>
    public int LastObservedState { get; private set; } = -1;

    /// <summary>Hand count snapshot from the last tick. For the overlay.</summary>
    public int LastObservedHandCount { get; private set; } = -1;

    public AutoPlayLoop(Plugin plugin, IFramework framework, IPluginLog log, MahjongAddon addon)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(addon);
        this.plugin = plugin;
        this.framework = framework;
        this.log = log;
        this.addon = addon;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        framework.Update -= OnUpdate;
    }

    private unsafe void OnUpdate(IFramework fw)
    {
        if (disposed)
            return;

        if (!IsAutomationArmed())
            return;

        if (!ContinueAfterStuckRecovery())
            return;

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null)
        {
            fsm.ClearContext();
            return;
        }

        int state = ReadStateCode();
        var context = new DispatchContext(state, snap.Hand.Count);
        LastObservedState = state;
        LastObservedHandCount = context.Hand;

        if (state == ChiVariantSelectStateCode)
        {
            HandleChiVariantSelect(context);
            return;
        }

        bool isCallPrompt = (snap.Legal.Flags & CallPromptFlags) != 0;
        bool isDiscardTurn = snap.Legal.Can(ActionFlags.Discard);

        // The riichi popup signature persists across the post-click yaku-preview
        // popup. Clear the latch when the prompt goes away or the Riichi flag
        // specifically falls off, so the next round starts fresh.
        if (!isCallPrompt || !snap.Legal.Can(ActionFlags.Riichi))
            fsm.ClearRiichiConfirm();

        if (!isCallPrompt && !isDiscardTurn)
        {
            fsm.ClearContext();
            return;
        }

        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
            return;

        if (TryHandleRiichiConfirmTsumogiri(snap, context, isCallPrompt))
            return;

        if (isCallPrompt)
            ScheduleCallDecision(context);
        else
            ScheduleDiscard(context);
    }

    private bool IsAutomationArmed()
    {
        var cfg = plugin.Configuration;
        return cfg.TosAccepted && cfg.AutomationArmed && !cfg.SuggestionOnly;
    }

    /// <summary>
    /// If a dispatch is in-flight, either bail this tick (still within timeout)
    /// or recover from stuck state. Returns true if the loop should continue
    /// processing this tick.
    /// </summary>
    private bool ContinueAfterStuckRecovery()
    {
        if (!fsm.IsDispatchInFlight)
            return true;
        if (fsm.TryRecoverFromStuckDispatch(DateTime.UtcNow))
        {
            log.Warning("[AutoPlayLoop] resetting stuck actionPending");
            return true;
        }
        return false;
    }

    private void HandleChiVariantSelect(DispatchContext context)
    {
        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
            return;
        ScheduleVariantAccept(context);
    }

    /// <summary>
    /// Post-declaration Riichi popup handling: when the riichi-confirm latch is
    /// set and the popup is still showing Riichi as legal with a 14-tile hand,
    /// complete the declaration via tsumogiri instead of re-clicking the list
    /// (which no-ops at this point).
    /// </summary>
    private bool TryHandleRiichiConfirmTsumogiri(StateSnapshot snap, DispatchContext context, bool isCallPrompt)
    {
        if (!fsm.IsRiichiConfirmPending)
            return false;
        if (!isCallPrompt || !snap.Legal.Can(ActionFlags.Riichi))
            return false;
        if (context.Hand <= 0 || context.Hand % 3 != 2)
            return false;

        ScheduleRiichiTsumogiri(context);
        return true;
    }

    // -----------------------------------------------------------------
    // Dispatch scheduling — every action goes through ScheduleAction so the
    // FSM begin/complete pair is the same shape every time.
    // -----------------------------------------------------------------

    private void ScheduleAction(string label, DispatchContext context, int medianDelayMs, Action body)
    {
        fsm.BeginDispatch(DateTime.UtcNow, context);
        var delay = HumanTiming.RandomDelay(medianMs: medianDelayMs);
        _ = framework.RunOnTick(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                log.Error($"AutoPlayLoop {label} error: {ex}");
                LastActionDescription = $"{label} exception: {ex.Message}";
            }
            finally
            {
                fsm.CompleteDispatch();
            }
        }, delay);
    }

    private void ScheduleVariantAccept(DispatchContext context)
    {
        ScheduleAction("variant", context, VariantAcceptDelayMs, () =>
        {
            // Re-check at dispatch time: the modal can close during the humanized
            // delay (auto-declare elsewhere, manual click, opponent timeout).
            int currentState = ReadStateCode();
            if (currentState != ChiVariantSelectStateCode)
            {
                LastActionDescription = $"variant aborted: state moved {ChiVariantSelectStateCode}→{currentState}";
                return;
            }
            var result = plugin.Dispatcher.DispatchCallOption(0);
            LastActionDescription = $"auto-variant[opt=0] → {result}";
            log.Info($"[AutoPlayLoop] variant dispatch: {LastActionDescription}");
            plugin.GameLogger.RecordAction(ActionKind.Chi, null, 0, result.ToString(), "chi-variant");
        });
    }

    private void ScheduleDiscard(DispatchContext context)
    {
        ScheduleAction("discard", context, plugin.Configuration.HumanizedDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null || !snap.Legal.Can(ActionFlags.Discard))
            {
                LastActionDescription = $"discard aborted: not a discard state (hand={snap?.Hand.Count ?? -1})";
                return;
            }

            var choice = plugin.Policy.Choose(snap);
            DispatchPolicyChoice(snap, choice);
        });
    }

    private void ScheduleCallDecision(DispatchContext context)
    {
        ScheduleAction("call", context, CallDecisionDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null)
            {
                LastActionDescription = "call: no snapshot";
                return;
            }
            DispatchCallChoice(snap, plugin.Policy.Choose(snap));
        });
    }

    private void ScheduleRiichiTsumogiri(DispatchContext context)
    {
        ScheduleAction("riichi-tsumogiri", context, RiichiTsumogiriDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null || snap.Hand.Count < 14)
            {
                LastActionDescription = $"riichi-tsumogiri aborted: hand={snap?.Hand.Count ?? -1}";
                return;
            }
            var tile = snap.Hand[13];
            var result = plugin.Dispatcher.DispatchDiscard(13);
            LastActionDescription = $"auto-riichi-tsumogiri {tile} slot=13 → {result}";
            log.Info($"[AutoPlayLoop] riichi-tsumogiri dispatch: {LastActionDescription}");
            plugin.GameLogger.RecordAction(ActionKind.Discard, tile, 13, result.ToString(), "riichi-tsumogiri");
            // Consumed — clear the latch so the next round/popup re-evaluates.
            fsm.ClearRiichiConfirm();
        });
    }

    // -----------------------------------------------------------------
    // Choice → dispatch translation
    // -----------------------------------------------------------------

    private void DispatchPolicyChoice(StateSnapshot snap, ActionChoice choice)
    {
        if (choice.Kind == ActionKind.Tsumo)
        {
            var result = plugin.Dispatcher.DispatchTsumo();
            LastActionDescription = $"auto-tsumo → {result}";
            plugin.GameLogger.RecordAction(ActionKind.Tsumo, null, null, result.ToString(), choice.Reasoning);
            return;
        }

        if (choice.Kind == ActionKind.AnKan && choice.DiscardTile is { } kanTile)
        {
            DispatchAnkan(snap, choice, kanTile);
            return;
        }

        if (choice.Kind != ActionKind.Discard && choice.Kind != ActionKind.Riichi)
        {
            LastActionDescription = $"policy returned {choice.Kind} — not dispatching";
            return;
        }
        if (choice.DiscardTile is null)
        {
            LastActionDescription = $"policy {choice.Kind} missing tile";
            return;
        }

        DispatchDiscardOrRiichi(snap, choice);
    }

    private void DispatchAnkan(StateSnapshot snap, ActionChoice choice, Tile kanTile)
    {
        int slot = InputDispatcher.FindSlotOfTile(kanTile, snap.Hand);
        if (slot < 0)
        {
            LastActionDescription = $"kan tile {kanTile} not in hand";
            return;
        }
        var result = plugin.Dispatcher.DispatchKan(slot);
        LastActionDescription = $"auto-ankan {kanTile} slot={slot} → {result}";
        plugin.GameLogger.RecordAction(ActionKind.AnKan, kanTile, slot, result.ToString(), choice.Reasoning);
    }

    private void DispatchDiscardOrRiichi(StateSnapshot snap, ActionChoice choice)
    {
        var tile = choice.DiscardTile!.Value;
        int slot = InputDispatcher.FindSlotOfTile(tile, snap.Hand);
        if (slot < 0)
        {
            LastActionDescription = $"tile {tile} not in hand";
            return;
        }

        var result = choice.Kind == ActionKind.Riichi
            ? plugin.Dispatcher.DispatchRiichi(slot)
            : plugin.Dispatcher.DispatchDiscard(slot);
        string actionName = choice.Kind == ActionKind.Riichi ? "riichi" : "discard";
        LastActionDescription = $"auto-{actionName} {tile} slot={slot} → {result}";
        plugin.GameLogger.RecordAction(choice.Kind, tile, slot, result.ToString(), choice.Reasoning);
    }

    private void DispatchCallChoice(StateSnapshot snap, ActionChoice choice)
    {
        var legal = snap.Legal;

        // Riichi popup: policy.Choose returns Pass because its Riichi branch
        // lives in the discard flow. If Riichi is offered on its own, we accept
        // — the user already committed by the time the popup appears.
        bool acceptRiichiPopup = choice.Kind == ActionKind.Pass && legal.Can(ActionFlags.Riichi);

        bool shouldAccept = acceptRiichiPopup || choice.Kind is
            ActionKind.Ron or ActionKind.Tsumo or
            ActionKind.Pon or ActionKind.Chi or
            ActionKind.MinKan or ActionKind.ShouMinKan;

        if (shouldAccept)
            DispatchAccept(choice, legal, acceptRiichiPopup);
        else
            DispatchPass(choice, legal);

        log.Info($"[AutoPlayLoop] call-prompt dispatch: {LastActionDescription}");
    }

    private void DispatchAccept(ActionChoice choice, LegalActions legal, bool acceptRiichiPopup)
    {
        var result = plugin.Dispatcher.DispatchCall();
        string label = acceptRiichiPopup ? "riichi-confirm" : choice.Kind.ToString().ToLowerInvariant();
        LastActionDescription = $"auto-{label} → {result}";
        var loggedKind = acceptRiichiPopup ? ActionKind.Riichi : choice.Kind;
        plugin.GameLogger.RecordAction(
            loggedKind, null, null, result.ToString(),
            acceptRiichiPopup ? "riichi-confirm" : choice.Reasoning);

        // Latch on for the post-riichi-confirm popup: the yaku-preview confirm
        // popup shares the Riichi-flag signature of the initial popup, so
        // without this flag the loop would retry-dispatch forever.
        if (acceptRiichiPopup)
            fsm.LatchRiichiConfirm();
    }

    private void DispatchPass(ActionChoice choice, LegalActions legal)
    {
        // Pass is always the rightmost button: its option index equals the
        // number of accept buttons shown. Multi-chi adds extra accept slots
        // — one per chi candidate.
        int passIndex = ComputePassIndex(legal);
        var result = plugin.Dispatcher.DispatchCallOption(passIndex);
        LastActionDescription = $"auto-pass[opt={passIndex}] → {result}";
        plugin.GameLogger.RecordAction(ActionKind.Pass, null, passIndex, result.ToString(), choice.Reasoning);
    }

    private static int ComputePassIndex(LegalActions legal)
    {
        int idx = 0;
        if (legal.Can(ActionFlags.Pon))
            idx++;
        if (legal.Can(ActionFlags.Chi))
            idx += Math.Max(1, legal.ChiCandidates.Count);
        if (legal.Can(ActionFlags.MinKan))
            idx++;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;
        if (legal.Can(ActionFlags.Ron))
            idx++;
        if (legal.Can(ActionFlags.Riichi))
            idx++;
        if (legal.Can(ActionFlags.Tsumo))
            idx++;
        return idx;
    }

    private unsafe int ReadStateCode()
    {
        if (!addon.TryGet(out var unit, out _))
            return -1;
        if (!unit->IsVisible || unit->AtkValues == null || unit->AtkValuesCount == 0)
            return -1;
        var v = unit->AtkValues[0];
        return v.Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int ? v.Int : -1;
    }
}
