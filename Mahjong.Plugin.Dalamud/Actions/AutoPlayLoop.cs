using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Logging;
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
    // One-shot diagnostic: when the tick decides to skip dispatch, log the
    // reason exactly once per transition (not 60×/sec). Lets dalamud.log
    // answer "why didn't auto-play fire" without manual repro on the
    // developer's machine — observed twice now (2026-05-18 22:30, 23:03)
    // where the plugin shows hints but auto-play stays silent and there's
    // zero signal in the log to triage.
    private string? lastSkipReason;

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
        {
            var cfg = plugin.Configuration;
            EmitSkipReason($"gate: tos={cfg.TosAccepted} armed={cfg.AutomationArmed} suggest_only={cfg.SuggestionOnly}",
                state: -1, hand: -1, flags: 0);
            return;
        }

        if (!ContinueAfterStuckRecovery())
        {
            EmitSkipReason("dispatch in flight (still within timeout)",
                state: -1, hand: -1, flags: 0);
            return;
        }

        var snap = plugin.AddonReader.TryBuildSnapshot();
        if (snap is null)
        {
            fsm.ClearContext();
            EmitSkipReason("snapshot unavailable", state: -1, hand: -1, flags: 0);
            return;
        }

        int state = ReadStateCode();
        var context = new DispatchContext(state, snap.Hand.Count);
        LastObservedState = state;
        LastObservedHandCount = context.Hand;

        if (state == ChiVariantSelectStateCode)
        {
            EmitProgressing();
            HandleChiVariantSelect(context);
            return;
        }

        bool isCallPrompt = (snap.Legal.Flags & CallPromptFlags) != 0;
        bool isDiscardTurn = snap.Legal.Can(ActionFlags.Discard);
        int flags = (int)snap.Legal.Flags;

        // Hand-scope the riichi-confirm latch. Earlier we cleared it on every
        // tick where the popup signature dropped, but popup signature DOES drop
        // briefly between adjacent ticks of the same hand (state code transitions
        // through neutral codes 19/22 etc), and that let the policy re-evaluate
        // riichi every time the popup re-appeared and stack 20+ "Riichi-confirm"
        // clicks in the same hand. ObserveWall clears the latch on the next
        // hand instead — within a hand once we declared, never re-prompt.
        fsm.ObserveWall(snap.WallRemaining);

        if (!isCallPrompt && !isDiscardTurn)
        {
            fsm.ClearContext();
            EmitSkipReason($"not actionable (state={state} hand={snap.Hand.Count} legal={snap.Legal.Flags})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (fsm.ShouldSuppressForContext(context, DateTime.UtcNow))
        {
            EmitSkipReason($"suppressed for context (state={context.State} hand={context.Hand})",
                state: state, hand: snap.Hand.Count, flags: flags);
            return;
        }

        if (TryHandleRiichiConfirmTsumogiri(snap, context, isCallPrompt))
        {
            EmitProgressing();
            return;
        }

        EmitProgressing();
        if (isCallPrompt)
            ScheduleCallDecision(context);
        else
            ScheduleDiscard(context);
    }

    /// <summary>
    /// Log one Plugin.Log.Info line per skip-reason transition AND emit a
    /// hand_state_paused finding for telemetry triage. The auto-play loop
    /// ticks 60×/sec, so emitting every skip would flood; this dedups by
    /// exact reason string so a steady "gate: tos=True armed=True
    /// suggest_only=True" lands as one entry, not 3600 per minute.
    /// </summary>
    private void EmitSkipReason(string reason, int state, int hand, int flags)
    {
        if (lastSkipReason == reason)
            return;
        lastSkipReason = reason;
        log.Info($"[AutoPlayLoop] skip: {reason}");
        plugin.FindingsLog?.Record("hand_state_paused", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["state"] = state,
            ["hand"] = hand,
            ["flags"] = flags,
        });
    }

    /// <summary>
    /// Clear the dedup latch when the loop is actively dispatching. The next
    /// skip — whatever its reason — will log as a fresh transition.
    /// </summary>
    private void EmitProgressing()
    {
        if (lastSkipReason is null)
            return;
        log.Info($"[AutoPlayLoop] resumed (was: {lastSkipReason})");
        lastSkipReason = null;
    }

    /// <summary>
    /// Record a structured `decision` finding for a single policy choice. Includes
    /// the chosen action kind, the picked tile (if any), and the legal context
    /// the policy was evaluating. Used by offline triage to spot policies that
    /// pick the wrong action shape for a given prompt.
    /// </summary>
    private void EmitDecisionFinding(string source, StateSnapshot snap, ActionChoice choice)
    {
        plugin.FindingsLog?.Record("decision", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["kind"] = choice.Kind.ToString(),
            ["tile"] = choice.DiscardTile?.ToString(),
            ["hand_count"] = snap.Hand.Count,
            ["flags"] = (int)snap.Legal.Flags,
            ["pon_candidates"] = snap.Legal.PonCandidates.Count,
            ["chi_candidates"] = snap.Legal.ChiCandidates.Count,
            ["kan_candidates"] = snap.Legal.KanCandidates.Count,
            ["wall"] = snap.WallRemaining,
            ["reasoning"] = choice.Reasoning,
        });
    }

    /// <summary>
    /// Record a structured `dispatch_attempted` finding for every InputDispatcher
    /// call. The corpus can then verify "policy decided X, dispatcher fired X,
    /// game accepted X" instead of guessing from interleaved log lines.
    /// </summary>
    private void EmitDispatchFinding(
        string label, InputDispatcher.DispatchResult result,
        int? option = null, Tile? tile = null, int? slot = null, int? state = null)
    {
        plugin.FindingsLog?.Record("dispatch_attempted", new Dictionary<string, object?>
        {
            ["label"] = label,
            ["result"] = result.ToString(),
            ["option"] = option,
            ["tile"] = tile?.ToString(),
            ["slot"] = slot,
            ["state"] = state,
        });
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
            EmitDispatchFinding("chi-variant", result, option: 0, state: currentState);
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
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                return;
            }

            var choice = plugin.Policy.Choose(snap);
            log.Info($"[AutoPlayLoop] discard body: state={context.State} hand={snap.Hand.Count} flags={snap.Legal.Flags} choice={choice.Kind} tile={choice.DiscardTile}");
            EmitDecisionFinding("discard", snap, choice);
            DispatchPolicyChoice(snap, choice);
            log.Info($"[AutoPlayLoop] discard body done: {LastActionDescription}");
        });
    }

    /// <summary>
    /// If the most recent dispatch returned a transient failure
    /// (<see cref="InputDispatcher.DispatchResult.HookFailed"/>), wipe the
    /// (state, hand) context the FSM uses to debounce retries. Without this
    /// the next 3 s of ticks observe the same context and skip dispatching,
    /// stranding the bot whenever a discard click misses (observed 2026-05-10
    /// 17:48: opcode-7 FireCallback against the post-chi list widget returned
    /// false → FSM suppressed retries → bot froze with the post-call popup
    /// still up).
    /// </summary>
    private void ClearRetryDebounceIfHookFailed(InputDispatcher.DispatchResult result)
    {
        if (result == InputDispatcher.DispatchResult.HookFailed)
            fsm.ClearContext();
    }

    private void ScheduleCallDecision(DispatchContext context)
    {
        ScheduleAction("call", context, CallDecisionDelayMs, () =>
        {
            var snap = plugin.AddonReader.TryBuildSnapshot();
            if (snap is null)
            {
                LastActionDescription = "call: no snapshot";
                log.Info($"[AutoPlayLoop] {LastActionDescription}");
                return;
            }
            var choice = plugin.Policy.Choose(snap);
            log.Info($"[AutoPlayLoop] call body: state={context.State} hand={snap.Hand.Count} flags={snap.Legal.Flags} choice={choice.Kind} tile={choice.DiscardTile}");
            EmitDecisionFinding("call", snap, choice);
            DispatchCallChoice(snap, choice);
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
            EmitDispatchFinding("riichi-tsumogiri", result, tile: tile, slot: 13);
            ClearRetryDebounceIfHookFailed(result);
            // Don't clear the latch here — once we've declared riichi we
            // must NOT let policy.Choose re-evaluate it later in the same
            // hand (it would, since OurRiichi is always false in our
            // snapshot today and the synthetic Discard|Riichi probe in
            // ResolveRiichiPopupAcceptance would happily approve again).
            // ObserveWall clears the latch on the next hand boundary.
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
            EmitDispatchFinding("tsumo", result);
            ClearRetryDebounceIfHookFailed(result);
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
        EmitDispatchFinding("ankan", result, tile: kanTile, slot: slot);
        ClearRetryDebounceIfHookFailed(result);
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
        EmitDispatchFinding(actionName, result, tile: tile, slot: slot);
        ClearRetryDebounceIfHookFailed(result);
    }

    private void DispatchCallChoice(StateSnapshot snap, ActionChoice choice)
    {
        var legal = snap.Legal;

        // State-6 SelfDeclareList at hand=14 is dual-use: the popup offers
        // Riichi/Tsumo/AnKan AND lists the closed hand as discardable items.
        // When the policy returns Discard (or a manual-tile Riichi), the user
        // wants to discard a specific tile from the list, not press "Pass" on
        // the Riichi/Tsumo offer. The list-widget click path
        // (DispatchDiscard → TryDispatchListItemClick → SelectItem) already
        // handles the list shell correctly, so route Discard/Riichi through
        // DispatchPolicyChoice instead of falling through to Pass.
        //
        // Without this gate AutoPlay would press opcode-11 option-1 (Pass)
        // every retry-cooldown, dismissing the Riichi offer without
        // discarding — the game stays at state=6 hand=14 forever and
        // auto-play silently stalls. Reproduced 2026-05-19 00:04 (live logs
        // showed the 3-second-cycle stall in v0.1.0.4 diagnostics).
        if (choice.Kind is ActionKind.Discard or ActionKind.Riichi
            && choice.DiscardTile.HasValue)
        {
            DispatchPolicyChoice(snap, choice);
            log.Info($"[AutoPlayLoop] discard-from-call-popup dispatch: {LastActionDescription}");
            return;
        }

        bool acceptRiichiPopup = ResolveRiichiPopupAcceptance(snap, choice, out var riichiReason);

        bool shouldAccept = acceptRiichiPopup || choice.Kind is
            ActionKind.Ron or ActionKind.Tsumo or
            ActionKind.Pon or ActionKind.Chi or
            ActionKind.MinKan or ActionKind.ShouMinKan;

        if (shouldAccept)
            DispatchAccept(choice, legal, acceptRiichiPopup, riichiReason!);
        else
            DispatchPass(choice, legal, riichiReason);

        log.Info($"[AutoPlayLoop] call-prompt dispatch: {LastActionDescription}");
    }

    /// <summary>
    /// Decide whether the loop should click "Riichi" on a Riichi-bearing call
    /// prompt. Three branches:
    /// <list type="bullet">
    ///   <item>Policy didn't return Pass, or Riichi isn't on offer: not our concern.</item>
    ///   <item>Riichi-confirm latch already set, or hand sits at a draw-pending count
    ///         (% 3 != 2): this is the post-click yaku-preview popup; auto-accept since
    ///         the user already committed during the initial popup.</item>
    ///   <item>Otherwise (initial popup with a 14/11/8-tile hand): re-run the policy
    ///         against a synthetic Discard|Riichi snapshot so <c>RiichiPolicy.Evaluate</c>
    ///         actually fires. The standard call branch in <c>EfficiencyPolicy.Choose</c>
    ///         skips it because <c>HasCallOffered</c> ignores Riichi, which let every
    ///         offered Riichi auto-accept regardless of wait quality, wall remaining, or
    ///         push/fold — the cause of the late-thin-riichi deal-ins observed
    ///         2026-05-09.</item>
    /// </list>
    /// </summary>
    private bool ResolveRiichiPopupAcceptance(StateSnapshot snap, ActionChoice choice, out string? probeReason)
    {
        probeReason = null;
        if (choice.Kind != ActionKind.Pass || !snap.Legal.Can(ActionFlags.Riichi))
            return false;

        if (fsm.IsRiichiConfirmPending || snap.Hand.Count == 0 || snap.Hand.Count % 3 != 2)
        {
            probeReason = "riichi-confirm";
            return true;
        }

        var probe = snap with
        {
            Legal = snap.Legal with
            {
                Flags = ActionFlags.Discard | ActionFlags.Riichi,
            },
        };
        var verdict = plugin.Policy.Choose(probe);
        if (verdict.Kind != ActionKind.Riichi)
        {
            probeReason = string.IsNullOrEmpty(verdict.Reasoning)
                ? "riichi declined by policy"
                : $"riichi declined: {verdict.Reasoning}";
            return false;
        }

        probeReason = string.IsNullOrEmpty(verdict.Reasoning) ? "riichi-accept" : verdict.Reasoning;
        return true;
    }

    private void DispatchAccept(ActionChoice choice, LegalActions legal, bool acceptRiichiPopup, string riichiReason)
    {
        // Compute the correct accept-button index for the chosen action kind.
        // Pre-fix, this always called DispatchCall() (opt=0), which works only
        // when the chosen kind is the leftmost button — i.e. when Pon is offered,
        // Pon was always selected even if the policy returned Chi/MinKan/Ron.
        // That misfire was the primary cause of the post-2026-05-18 auto-play
        // regression: on every Pon+Chi simultaneous prompt the loop force-fired
        // Pon, and on every multi-chi prompt the loop picked the leftmost chi
        // variant even when the policy preferred a different sequence.
        var loggedKind = acceptRiichiPopup ? ActionKind.Riichi : choice.Kind;
        int acceptIndex = acceptRiichiPopup
            ? ComputeAcceptIndex(ActionKind.Riichi, legal, choice.Call)
            : ComputeAcceptIndex(choice.Kind, legal, choice.Call);
        var result = plugin.Dispatcher.DispatchCallOption(acceptIndex);
        string label = acceptRiichiPopup ? "riichi-confirm" : choice.Kind.ToString().ToLowerInvariant();
        LastActionDescription = $"auto-{label}[opt={acceptIndex}] → {result}";
        plugin.GameLogger.RecordAction(
            loggedKind, null, acceptIndex, result.ToString(),
            acceptRiichiPopup ? riichiReason : choice.Reasoning);
        EmitDispatchFinding(label, result, option: acceptIndex);

        // Latch on for the post-riichi-confirm popup: the yaku-preview confirm
        // popup shares the Riichi-flag signature of the initial popup, so
        // without this flag the loop would retry-dispatch forever.
        if (acceptRiichiPopup)
            fsm.LatchRiichiConfirm();
    }

    private void DispatchPass(ActionChoice choice, LegalActions legal, string? reasonOverride = null)
    {
        // Pass is always the rightmost button: its option index equals the
        // number of accept buttons shown. Multi-chi adds extra accept slots
        // — one per chi candidate.
        int passIndex = ComputePassIndex(legal);
        var result = plugin.Dispatcher.DispatchCallOption(passIndex);
        LastActionDescription = $"auto-pass[opt={passIndex}] → {result}";
        string reasoning = string.IsNullOrEmpty(reasonOverride) ? choice.Reasoning : reasonOverride;
        plugin.GameLogger.RecordAction(ActionKind.Pass, null, passIndex, result.ToString(), reasoning);
        EmitDispatchFinding("pass", result, option: passIndex);
    }

    /// <summary>
    /// Compute the button index for an accept-side action on a call-prompt popup.
    /// The button order is fixed by the addon: Pon, then one slot per Chi variant,
    /// then MinKan, ShouMinKan, Ron, Riichi, Tsumo, and finally Pass.
    /// </summary>
    /// <param name="kind">The action the policy decided to take.</param>
    /// <param name="legal">The legal-action context (flag set + candidate lists).</param>
    /// <param name="chosenCall">Optional: for Chi, the specific candidate the policy
    /// picked. Used to find the right chi-variant button index when the prompt
    /// offers multiple chi sequences. Null falls back to the first variant (opt 0
    /// among the chi slots).</param>
    internal static int ComputeAcceptIndex(ActionKind kind, LegalActions legal, MeldCandidate? chosenCall)
    {
        int idx = 0;

        if (kind == ActionKind.Pon)
            return idx;
        if (legal.Can(ActionFlags.Pon))
            idx++;

        if (kind == ActionKind.Chi)
        {
            int variant = ResolveChiVariantIndex(legal, chosenCall);
            return idx + variant;
        }
        if (legal.Can(ActionFlags.Chi))
            idx += Math.Max(1, legal.ChiCandidates.Count);

        if (kind == ActionKind.MinKan)
            return idx;
        if (legal.Can(ActionFlags.MinKan))
            idx++;

        if (kind == ActionKind.ShouMinKan)
            return idx;
        if (legal.Can(ActionFlags.ShouMinKan))
            idx++;

        if (kind == ActionKind.Ron)
            return idx;
        if (legal.Can(ActionFlags.Ron))
            idx++;

        if (kind == ActionKind.Riichi)
            return idx;
        if (legal.Can(ActionFlags.Riichi))
            idx++;

        if (kind == ActionKind.Tsumo)
            return idx;

        // Unknown action shape for an accept: fall back to leftmost. The dispatcher
        // would have done this before the fix too, so we preserve the legacy
        // behavior as the safety net rather than silently swallowing the click.
        return 0;
    }

    /// <summary>
    /// Find the index of the chosen chi candidate within
    /// <see cref="LegalActions.ChiCandidates"/>. Matched by structural equality on
    /// the claimed tile + hand tiles. Returns 0 if no match (the legacy default).
    /// </summary>
    private static int ResolveChiVariantIndex(LegalActions legal, MeldCandidate? chosenCall)
    {
        if (chosenCall is not { } call)
            return 0;
        var chi = legal.ChiCandidates;
        for (int i = 0; i < chi.Count; i++)
            if (ChiCandidateMatches(chi[i], call))
                return i;
        return 0;
    }

    private static bool ChiCandidateMatches(MeldCandidate a, MeldCandidate b)
    {
        if (a.Kind != b.Kind)
            return false;
        if (a.ClaimedTile.Id != b.ClaimedTile.Id)
            return false;
        if (a.HandTiles.Length != b.HandTiles.Length)
            return false;
        for (int i = 0; i < a.HandTiles.Length; i++)
            if (a.HandTiles[i].Id != b.HandTiles[i].Id)
                return false;
        return true;
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
