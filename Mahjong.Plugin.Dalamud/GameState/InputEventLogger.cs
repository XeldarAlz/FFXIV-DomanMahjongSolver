using System;
using System.Globalization;
using System.IO;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Records every <c>PostReceiveEvent</c> the Emj addon sees into a rolling log file.
/// Used to reverse-engineer the click-dispatch API: once a human plays a move
/// (discard tile, pon, pass, riichi, etc.), the log captures the addon's callback
/// arguments so we can replay them programmatically in M6.
///
/// Output: <c>%AppData%\XIVLauncher\pluginConfigs\Mahjong.Plugin.Dalamud\emj-events.log</c>.
/// Each line: <c>UTC  event=X  param=Y  args=[...]  hand=...</c>.
///
/// Enable/disable via <see cref="Enabled"/>. Off by default so we don't spam the log
/// during normal play; flip it on when doing RE sessions.
/// </summary>
public sealed class InputEventLogger : IDisposable
{
    // AtkUnitBase::FireCallback — signature from FFXIVClientStructs:
    //   bool FireCallback(uint valueCount, AtkValue* values, bool close)
    // Sig covers the callsite; Dalamud's HookFromSignature follows the E8 to the real function.
    private const string FireCallbackSig = "E8 ?? ?? ?? ?? 0F B6 E8 8B 44 24 20";
    private unsafe delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, byte close);

    private const double CaptureTimeoutSeconds = 60.0;

    private readonly AddonEmjReader reader;
    private readonly MeldTracker meldTracker;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameInteropProvider gameInterop;
    private readonly IPluginLog log;
    private readonly MahjongAddon addon;
    private readonly string logPath;
    private readonly string capturePath;
    private StreamWriter? writer;
    private bool disposed;
    private unsafe Hook<FireCallbackDelegate>? fireCallbackHook;

    /// <summary>Backing field for <see cref="PendingCaptureLabel"/>. Use the public
    /// property — its getter expires stale labels lazily so the user-facing status
    /// matches the actual capture behavior.</summary>
    private string? pendingCaptureLabel;
    private DateTime captureArmedAt;

    /// <summary>Label of the next FireCallback to record verbatim into the dedicated
    /// capture log, or null if not armed / expired. Cleared after one capture, on
    /// timeout (lazy — the getter clears stale labels on access), or via
    /// <see cref="DisarmCapture"/>. Used to RE the click payload for actions whose
    /// opcodes we don't yet know (riichi, tsumo, ron, ankan, shouminkan).</summary>
    public string? PendingCaptureLabel
    {
        get
        {
            if (pendingCaptureLabel is not null
                && (DateTime.UtcNow - captureArmedAt).TotalSeconds > CaptureTimeoutSeconds)
            {
                pendingCaptureLabel = null;
            }
            return pendingCaptureLabel;
        }
    }

    public bool Enabled { get; set; }

    public string CaptureLogPath => capturePath;

    /// <summary>
    /// Fires once per FireCallback the Mahjong addon receives, AFTER the
    /// original game callback runs (so subscribers see the post-call result).
    /// Values are snapshot to a managed array before firing, so subscribers
    /// can safely persist/log them without worrying about pointer lifetimes.
    /// Always-on regardless of <see cref="Enabled"/> — that flag only gates
    /// the verbose RE log.
    /// </summary>
    public event Action<InputCallbackEvent>? CallbackObserved;

    /// <summary>
    /// Fires once per call-prompt transition observed by a variant — i.e.
    /// when the addon enters its CallPrompt state code with at least one
    /// actionable flag (pon/chi/kan/ron/riichi/tsumo). The variant raises
    /// it from inside <c>TryBuildSnapshot</c> after AtkValues have been
    /// snapshot to a managed array. Always-on regardless of
    /// <see cref="Enabled"/>; the diagnostic file write inside the variant
    /// is the only thing the flag still gates.
    ///
    /// <para>GameLogger subscribes and emits a <c>call-prompt</c> NDJSON
    /// event into the active hand file, so the games stream carries the
    /// raw AtkValues window alongside the decoded candidate list — that
    /// pair is what we need to debug variant-decode bugs (e.g. the chi
    /// claim tile in #34) from uploaded telemetry rather than asking
    /// users to grab manual captures.</para>
    /// </summary>
    public event Action<CallPromptEvent>? CallPromptObserved;

    internal void RaiseCallPrompt(CallPromptEvent evt)
    {
        if (CallPromptObserved is not { } observers)
            return;
        try
        { observers(evt); }
        catch (Exception ex)
        { log.Error($"CallPromptObserved subscriber error: {ex.Message}"); }
    }

    public unsafe InputEventLogger(
        AddonEmjReader reader,
        MeldTracker meldTracker,
        IAddonLifecycle addonLifecycle,
        IGameInteropProvider gameInterop,
        IPluginLog log,
        MahjongAddon addon,
        string pluginConfigDir)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(meldTracker);
        ArgumentNullException.ThrowIfNull(addonLifecycle);
        ArgumentNullException.ThrowIfNull(gameInterop);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(addon);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        this.reader = reader;
        this.meldTracker = meldTracker;
        this.addonLifecycle = addonLifecycle;
        this.gameInterop = gameInterop;
        this.log = log;
        this.addon = addon;

        Directory.CreateDirectory(pluginConfigDir);
        logPath = Path.Combine(pluginConfigDir, "emj-events.log");
        capturePath = Path.Combine(pluginConfigDir, "emj-captures.log");

        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, MahjongAddon.CandidateNames, OnReceiveEvent);

        // Install a global FireCallback hook; we filter by addon name inside the detour.
        try
        {
            fireCallbackHook = gameInterop.HookFromSignature<FireCallbackDelegate>(
                FireCallbackSig, FireCallbackDetour);
            fireCallbackHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error($"InputEventLogger: failed to hook FireCallback: {ex}");
            fireCallbackHook = null;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        addonLifecycle.UnregisterListener(OnReceiveEvent);
        fireCallbackHook?.Disable();
        fireCallbackHook?.Dispose();
        fireCallbackHook = null;
        writer?.Flush();
        writer?.Dispose();
    }

    public string LogPath => logPath;

    /// <summary>
    /// Arm a one-shot capture: the next FireCallback fired against the Emj addon will
    /// be appended verbatim to <c>emj-captures.log</c> under <paramref name="label"/>.
    /// Auto-clears after one capture or after <see cref="CaptureTimeoutSeconds"/>
    /// seconds with no click — so a stray UI interaction days later won't be tagged.
    /// Re-arming overwrites any pending label.
    /// </summary>
    public void ArmCapture(string label)
    {
        pendingCaptureLabel = label;
        captureArmedAt = DateTime.UtcNow;
    }

    /// <summary>Cancel a pending capture without recording anything.</summary>
    public void DisarmCapture()
    {
        pendingCaptureLabel = null;
    }

    public void OpenLog()
    {
        writer ??= new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    public void CloseLog()
    {
        writer?.Flush();
        writer?.Dispose();
        writer = null;
    }

    private unsafe bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, byte close)
    {
        // Determine meld-accept intent BEFORE the game processes the click so the Legal
        // snapshot still reflects the offered candidates. opcode 11 + option 0 = accept
        // leftmost button on a call prompt (pon / chi / min-kan). For multi-variant chi
        // we'd want the specific variant picked but the game only ever takes option 0
        // from us today (matches what DispatchCall() sends).
        MeldCandidate? acceptedMeld = null;
        if (addon != null && MahjongAddon.IsMahjongAddon(addon->NameString)
            && valueCount == 2
            && values[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int
            && values[0].Int == 11
            && values[1].Type == FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int
            && values[1].Int == 0)
        {
            var preSnap = reader.TryBuildSnapshot();
            if (preSnap is not null)
            {
                if (preSnap.Legal.PonCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.PonCandidates[0];
                else if (preSnap.Legal.ChiCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.ChiCandidates[0];
                else if (preSnap.Legal.KanCandidates.Count > 0)
                    acceptedMeld = preSnap.Legal.KanCandidates[0];
            }
        }

        // Capture snapshot — must run BEFORE the original FireCallback. The original may
        // mutate addon state (close a modal, refresh AtkValues), so reading post-call
        // would record post-click state instead of the at-click context we want for RE.
        // Both the addon AtkValues and the fire_args are formatted into managed strings
        // here so the captured payload stays valid even if the caller's buffers move.
        string? captureLabel = null;
        string? captureHand = null;
        string[]? captureFireArgs = null;
        string[]? captureAtkValues = null;
        int captureAtkCount = 0;
        if (PendingCaptureLabel is { } pending
            && addon != null && MahjongAddon.IsMahjongAddon(addon->NameString))
        {
            captureLabel = pending;
            captureFireArgs = SnapshotValues(values, (int)valueCount, max: 32);
            captureAtkCount = addon->AtkValuesCount;
            captureAtkValues = SnapshotValues(addon->AtkValues, captureAtkCount, max: 64);
            var preSnap = reader.TryBuildSnapshot();
            if (preSnap is not null && preSnap.Hand.Count > 0)
                captureHand = Tiles.Render(preSnap.Hand);
        }

        // Always call the original FIRST so game logic is unaffected regardless of logger state.
        bool result = fireCallbackHook!.Original(addon, valueCount, values, close);

        // Record the meld on every opcode-11/option-0 dispatch, not gated on result.
        // FireCallback returns false for this opcode even when the game accepts the
        // click (verified by capturing manual in-game pon/chi/riichi presses — all
        // logged result=False despite the calls actually firing). Gating on result
        // here desynced MeldTracker from reality: after every pon, closed-hand ran
        // 3 tiles ahead of what we'd recorded, and AutoPlayLoop's % 3 == 2 discard
        // check eventually rejected our turn as "not a discard state" — the real
        // root cause of the "plays 1-2 rounds then freezes" report in #9.
        if (acceptedMeld is { } meld)
        {
            try
            { meldTracker.Record(Meld.FromAcceptedCandidate(meld)); }
            catch (Exception ex) { log.Error($"MeldTracker record error: {ex.Message}"); }
        }

        // Always-on managed event for telemetry subscribers (InputRecorder ships
        // these to the inputs/ stream). Snapshot int values now so the subscriber
        // never sees the unmanaged AtkValue pointer.
        if (CallbackObserved is { } observers
            && addon != null && MahjongAddon.IsMahjongAddon(addon->NameString))
        {
            try
            {
                var ints = SnapshotInts(values, (int)valueCount, max: 24);
                observers(new InputCallbackEvent(
                    ObservedAtUtc: DateTime.UtcNow,
                    AddonName: addon->NameString,
                    ValueCount: valueCount,
                    Close: close != 0,
                    Result: result,
                    IntValues: ints));
            }
            catch (Exception ex)
            {
                log.Error($"FireCallback observer error: {ex.Message}");
            }
        }

        try
        {
            if (Enabled && addon != null && MahjongAddon.IsMahjongAddon(addon->NameString))
            {
                OpenLog();
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.Append($"  evt=FireCallback  count={valueCount}  close={(close != 0)}  result={result}");

                var snap = reader.TryBuildSnapshot();
                if (snap is not null && snap.Hand.Count > 0)
                {
                    sb.Append("  hand=");
                    sb.Append(Tiles.Render(snap.Hand));
                }

                if (values != null && valueCount > 0)
                {
                    sb.Append("  values=[");
                    uint cap = valueCount > 16 ? 16 : valueCount;
                    for (uint i = 0; i < cap; i++)
                    {
                        var v = values[i];
                        sb.Append($"{i}:{v.Type}=");
                        switch (v.Type)
                        {
                            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int:
                                sb.Append(v.Int);
                                break;
                            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt:
                                sb.Append(v.UInt);
                                break;
                            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool:
                                sb.Append(v.Byte != 0);
                                break;
                            default:
                                sb.Append($"raw=0x{v.UInt:X}");
                                break;
                        }
                        if (i < cap - 1)
                            sb.Append(',');
                    }
                    if (valueCount > cap)
                        sb.Append($"...+{valueCount - cap}");
                    sb.Append(']');
                }

                writer?.WriteLine(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            log.Error($"FireCallback log error: {ex.Message}");
        }

        // One-shot capture: write out the pre-call snapshot (taken above) plus the
        // now-known result, then disarm. Used for opcode RE — the user runs
        // `/mjauto capture riichi`, clicks the riichi button, and gets a labeled entry.
        if (captureLabel is not null)
        {
            try
            {
                WriteCaptureEntry(
                    captureLabel, captureHand, captureFireArgs!, valueCount,
                    captureAtkValues!, captureAtkCount, close, result);
            }
            catch (Exception ex)
            {
                log.Error($"FireCallback capture error: {ex.Message}");
            }
            finally
            {
                pendingCaptureLabel = null;
            }
        }

        return result;
    }

    private unsafe void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!Enabled)
            return;
        OpenLog();

        var addr = args.Addon.Address;
        if (addr == 0)
            return;

        var sb = new StringBuilder();
        sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        sb.Append("  evt=PostReceiveEvent");

        if (args is AddonReceiveEventArgs rea)
        {
            sb.Append($"  type={rea.AtkEventType}  param={rea.EventParam}");
        }

        // Snapshot hand so we can correlate a click with the hand shape at click time.
        var snap = reader.TryBuildSnapshot();
        if (snap is not null && snap.Hand.Count > 0)
        {
            sb.Append("  hand=");
            sb.Append(Tiles.Render(snap.Hand));
        }

        // Dump the first few AtkValues — some addons push context through here.
        var unit = (AtkUnitBase*)addr;
        int valueCount = Math.Min((int)unit->AtkValuesCount, 8);
        if (valueCount > 0)
        {
            sb.Append("  atk=[");
            for (int i = 0; i < valueCount; i++)
            {
                var v = unit->AtkValues[i];
                sb.Append($"{i}:{v.Type}=");
                switch (v.Type)
                {
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int:
                        sb.Append(v.Int);
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt:
                        sb.Append(v.UInt);
                        break;
                    case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool:
                        sb.Append(v.Byte != 0);
                        break;
                    default:
                        sb.Append("?");
                        break;
                }
                if (i < valueCount - 1)
                    sb.Append(',');
            }
            sb.Append(']');
        }

        writer?.WriteLine(sb.ToString());
    }

    /// <summary>
    /// Append a single annotated capture entry from a pre-call snapshot. The lines for
    /// fire_args and addon AtkValues are formatted by <see cref="SnapshotValues"/>
    /// before the original FireCallback runs, so they reflect the at-click state even
    /// if the original mutates the addon. File is grep-friendly: each entry starts
    /// with a <c># label=...</c> header.
    /// </summary>
    private void WriteCaptureEntry(
        string label, string? hand, string[] fireArgs, uint fireArgCount,
        string[] atkValues, int atkCount, byte close, bool result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"# {DateTime.UtcNow:o}  label={label}  result={result}  close={(close != 0)}  " +
            $"valueCount={fireArgCount}");

        if (hand is not null)
            sb.AppendLine($"hand={hand}");

        sb.AppendLine($"fire_args (count={fireArgCount}):");
        for (int i = 0; i < fireArgs.Length; i++)
            sb.AppendLine($"  [{i,3}] {fireArgs[i]}");
        if (fireArgCount > fireArgs.Length)
            sb.AppendLine($"  ... +{fireArgCount - fireArgs.Length} more");

        sb.AppendLine($"addon_atkvalues (count={atkCount}):");
        for (int i = 0; i < atkValues.Length; i++)
            sb.AppendLine($"  [{i,3}] {atkValues[i]}");
        if (atkCount > atkValues.Length)
            sb.AppendLine($"  ... +{atkCount - atkValues.Length} more");

        sb.AppendLine();
        File.AppendAllText(capturePath, sb.ToString());
        log.Info(
            $"[capture] recorded label={label} (result={result}) → {capturePath}");
    }

    /// <summary>
    /// Format up to <paramref name="max"/> AtkValues into managed strings while we
    /// still have valid pointers. Strings are decoded eagerly so the captured payload
    /// stays correct after the original FireCallback returns and the source memory may
    /// have been reused. Returns an empty array if <paramref name="values"/> is null.
    /// </summary>
    private static unsafe string[] SnapshotValues(AtkValue* values, int count, int max)
    {
        if (values == null || count <= 0)
            return Array.Empty<string>();
        int n = Math.Min(count, max);
        var result = new string[n];
        for (int i = 0; i < n; i++)
            result[i] = FormatValue(values[i]);
        return result;
    }

    /// <summary>
    /// Snapshot up to <paramref name="max"/> AtkValues into an int?[] for the
    /// always-on telemetry path. Non-numeric AtkValue types serialize as null
    /// so the subscriber doesn't have to know about AtkValueType at all —
    /// what we care about for input recording is the action opcode + option
    /// pair (always Int) plus the call-claim slots Doman packs into the
    /// [16..21] range (chiClaimedTile at 19, pon-duplicate scan range 16..21,
    /// kanClaimedTile follow-up). Strings/floats are dropped intentionally.
    /// </summary>
    private static unsafe int?[] SnapshotInts(AtkValue* values, int count, int max)
    {
        if (values == null || count <= 0)
            return Array.Empty<int?>();
        int n = Math.Min(count, max);
        var result = new int?[n];
        for (int i = 0; i < n; i++)
        {
            var v = values[i];
            result[i] = v.Type switch
            {
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int => v.Int,
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt => unchecked((int)v.UInt),
                FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool => v.Byte != 0 ? 1 : 0,
                _ => (int?)null,
            };
        }
        return result;
    }

    private static unsafe string FormatValue(AtkValue v)
    {
        switch (v.Type)
        {
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int:
                return $"{v.Type,-14} Int={v.Int}";
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt:
                return $"{v.Type,-14} UInt={v.UInt} (0x{v.UInt:X})";
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool:
                return $"{v.Type,-14} Bool={v.Byte != 0}";
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String:
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8:
            case FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString:
                var s = v.String.Value != null
                    ? v.String.ToString()
                    : "(null)";
                return $"{v.Type,-14} String=\"{s}\"";
            default:
                return $"{v.Type,-14} raw=0x{v.UInt:X}";
        }
    }
}

/// <summary>
/// Managed snapshot of one FireCallback dispatched to the Mahjong addon.
/// Fired by <see cref="InputEventLogger.CallbackObserved"/> after the original
/// game callback runs. Only int-coercible AtkValues survive the snapshot —
/// the action opcode + option pair is always int-typed, and that's what
/// downstream consumers (input telemetry, ML training data) care about.
/// </summary>
public sealed record InputCallbackEvent(
    DateTime ObservedAtUtc,
    string AddonName,
    uint ValueCount,
    bool Close,
    bool Result,
    int?[] IntValues);

/// <summary>
/// Managed snapshot of one call-prompt transition observed by a variant —
/// when the addon enters its CallPrompt state code with at least one
/// actionable flag (pon/chi/kan/ron/riichi/tsumo). Carries both the raw
/// AtkValues window the variant decoded *from* and the candidate tile-id
/// arrays it decoded *to*, so any divergence (e.g. AtkValues[19] holding a
/// tile id that doesn't match the resulting ChiCandidates entry) is
/// reconstructible from telemetry alone.
/// </summary>
public sealed record CallPromptEvent(
    DateTime ObservedAtUtc,
    string AddonName,
    int StateCode,
    int Flags,                       // (int)LegalActions.Flags at the prompt
    int[] PonClaimedTileIds,
    int[] ChiClaimedTileIds,
    int[] KanClaimedTileIds,
    int?[] IntValues);               // [0..N) raw AtkValue ints, capped same as InputCallbackEvent
