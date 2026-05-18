using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Engine;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Logging;

namespace Mahjong.Plugin.Dalamud.GameState;

/// <summary>
/// Finds the Mahjong addon in the running client, subscribes to its lifecycle
/// events, and exposes:
///   - a raw <see cref="AddonEmjObservation"/> (for diagnostics and the debug overlay)
///   - a <see cref="StateSnapshot"/> builder that delegates to the selected
///     <see cref="IEmjVariant"/> for layout-specific reads.
///
/// This component owns the addon-lifecycle wiring and the observation record
/// (both framework-level and variant-agnostic). Every offset / node ID /
/// AtkValue slot lives in a <see cref="LayoutProfile"/> loaded from
/// <c>layouts/*.json</c> next to the plugin assembly.
///
/// Must be created on (and disposed from) the framework thread.
/// </summary>
public sealed class AddonEmjReader : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly MahjongAddon addon;
    private readonly MeldTracker meldTracker;
    private readonly VariantSelector selector;
    private readonly IFindingsLog? findings;
    private bool disposed;
    // Reset on every PreFinalize so each attach/unload cycle (≈ one match)
    // produces a paired addon_lifecycle / addon_unload pair in the corpus.
    private bool emittedFirstLifecycle;
    // Per-session one-shots — these only need to fire once per plugin load
    // since the underlying anomaly is usually a stable schema/build issue,
    // not a transient state. Emitting more than once would just bloat the
    // corpus with dupes.
    private bool emittedDimensionsZero;
    private bool emittedAtkValuesAnomaly;
    // Tracks Poll()'s last reported presence so we can emit only on
    // transitions, not every tick. Null = never polled.
    private bool? lastPollPresent;
    // Track build-failure streaks so we emit one finding per failure window
    // (and one per recovery), not one per tick. Most failures recur every
    // frame for several seconds — flooding the corpus would be wasteful and
    // bury more useful signals.
    private bool inSnapshotFailureStreak;

    /// <summary>
    /// Set by Plugin.cs after both AddonReader and InputEventLogger are
    /// constructed (they would otherwise be a constructor-order cycle —
    /// EventLogger needs the reader, the reader's snapshot path needs the
    /// logger). Null until the wire-up completes; <c>TryBuildSnapshot</c>
    /// guards on it.
    /// </summary>
    public InputEventLogger? EventLogger { get; set; }

    /// <summary>
    /// The variant selector in use. Exposed for diagnostic surfaces
    /// (<c>/mjauto variant dump</c>) that need to enumerate registered
    /// variants and re-probe them on demand.
    /// </summary>
    internal VariantSelector Selector => selector;

    public AddonEmjObservation LastObservation { get; private set; } = AddonEmjObservation.Empty;

    /// <summary>
    /// The most recently-resolved variant's layout profile, or null if no
    /// variant has resolved this session. Set inside <see cref="TryBuildSnapshot"/>
    /// the first time a variant successfully matches the live addon, and updated
    /// on every subsequent successful resolution. Read by the memory-dump
    /// recorder so each snapshot can travel with the seat-offset map that
    /// describes how to slice <c>addon_b64</c>.
    /// </summary>
    public LayoutProfile? ActiveLayout { get; private set; }

    /// <summary>Fired whenever any lifecycle event updates the observation.</summary>
    public event Action<AddonEmjObservation>? ObservationChanged;

    public AddonEmjReader(
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        MahjongAddon addon,
        MeldTracker meldTracker,
        string pluginConfigDir,
        string layoutsDir,
        IFindingsLog? findings = null)
    {
        ArgumentNullException.ThrowIfNull(addonLifecycle);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(addon);
        ArgumentNullException.ThrowIfNull(meldTracker);
        ArgumentException.ThrowIfNullOrEmpty(pluginConfigDir);
        ArgumentException.ThrowIfNullOrEmpty(layoutsDir);
        this.addonLifecycle = addonLifecycle;
        this.log = log;
        this.addon = addon;
        this.meldTracker = meldTracker;
        this.findings = findings;

        selector = new VariantSelector(LoadRegisteredVariants(log, pluginConfigDir, layoutsDir, findings), log, findings);

        // Register against every known Mahjong addon name (issue #13): some clients
        // expose "Emj", others "EmjL". Whichever one exists locally will fire — the
        // other is a silent no-op. The injected MahjongAddon resolves the live pointer.
        var names = MahjongAddon.CandidateNames;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, names, OnPostSetup);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, names, OnPreFinalize);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, names, OnPostRefresh);
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, names, OnPostReceiveEvent);
    }

    /// <summary>
    /// Discover and load every <c>layouts/*.json</c> next to the plugin
    /// assembly, and return one <see cref="BaseEmjVariant"/> per profile.
    /// Order is deterministic (filename ascending) so the variant selector's
    /// arbitrary tiebreaker is stable across runs.
    ///
    /// On any IO / parse error, logs and returns an empty list — the selector
    /// then reports "No Emj variant matched" loudly via its existing path.
    /// </summary>
    private static IReadOnlyList<IEmjVariant> LoadRegisteredVariants(
        IPluginLog log, string pluginConfigDir, string layoutsDir, IFindingsLog? findings)
    {
        try
        {
            var profiles = JsonLayoutProfileLoader.LoadAll(layoutsDir);
            var variants = new List<IEmjVariant>(profiles.Count);
            foreach (var p in profiles)
                variants.Add(new BaseEmjVariant(p, log, pluginConfigDir));
            log.Info(
                $"[MjAuto] Loaded {variants.Count} layout profile(s) from {layoutsDir}: " +
                $"{string.Join(", ", variants.ConvertAll(v => v.Name))}");
            findings?.Record("layouts_loaded", new Dictionary<string, object?>
            {
                ["dir"] = PathRedactor.Redact(layoutsDir),
                ["count"] = variants.Count,
                ["names"] = variants.Select(v => v.Name).ToArray(),
            });
            return variants;
        }
        catch (Exception ex)
        {
            log.Error($"[MjAuto] Layout profile load failed at {layoutsDir}: {ex.Message}");
            findings?.Record("layouts_load_fail", new Dictionary<string, object?>
            {
                ["dir"] = PathRedactor.Redact(layoutsDir),
                ["exception_type"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });
            return [];
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        addonLifecycle.UnregisterListener(OnPostSetup);
        addonLifecycle.UnregisterListener(OnPreFinalize);
        addonLifecycle.UnregisterListener(OnPostRefresh);
        addonLifecycle.UnregisterListener(OnPostReceiveEvent);
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args) => Observe("PostSetup", args);
    private void OnPostRefresh(AddonEvent type, AddonArgs args) => Observe("PostRefresh", args);
    private void OnPostReceiveEvent(AddonEvent type, AddonArgs args) => Observe("PostReceiveEvent", args);

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
        // Emit unload paired with the most recent addon_lifecycle finding.
        // Carries through visibility-at-teardown (sometimes the addon goes
        // invisible just before unloading; sometimes it's still drawing).
        // Then reset emittedFirstLifecycle so the next attach emits a fresh
        // paired event for the next match.
        if (LastObservation.Present)
        {
            findings?.Record("addon_unload", new Dictionary<string, object?>
            {
                ["addon_name"] = args.AddonName,
                ["was_visible"] = LastObservation.IsVisible,
                ["last_address"] = LastObservation.Address.ToInt64(),
                ["last_event"] = LastObservation.LastLifecycleEvent,
            });
        }
        emittedFirstLifecycle = false;

        LastObservation = AddonEmjObservation.Empty with { LastLifecycleEvent = "PreFinalize" };
        ObservationChanged?.Invoke(LastObservation);
    }

    private unsafe void Observe(string eventName, AddonArgs args)
    {
        var addr = args.Addon.Address;
        var obs = AddonEmjObservation.Empty;

        if (addr != 0)
        {
            var unit = (AtkUnitBase*)addr;
            obs = new AddonEmjObservation(
                Present: true,
                IsVisible: unit->IsVisible,
                Address: addr,
                Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
                Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
                LastSeenUtcTicks: DateTime.UtcNow.Ticks,
                LastLifecycleEvent: eventName);

            EmitFirstAttachFindings(eventName, args.AddonName, unit, addr, obs);
        }

        LastObservation = obs;
        ObservationChanged?.Invoke(obs);
    }

    /// <summary>
    /// One-shot per-attach findings emission. Called from both
    /// <see cref="Observe"/> (Dalamud lifecycle events) and <see cref="Poll"/>
    /// (GameGui-driven discovery). Without the Poll path firing this, ~70% of
    /// installs in the 2026-05-11 corpus shipped zero gameplay logs — those
    /// installs loaded the plugin with the addon already open, so no
    /// PostSetup event ever fired and the lifecycle/anomaly findings (which
    /// downstream analyzers use as session-start markers) never landed.
    ///
    /// <para>Each emit is gated on a per-session bool, so repeat calls (Poll
    /// fires every tick) only land the first event for the current attach.
    /// The PreFinalize handler resets <c>emittedFirstLifecycle</c> so the
    /// NEXT match emits a fresh lifecycle pair.</para>
    /// </summary>
    private unsafe void EmitFirstAttachFindings(
        string eventName, string addonName, AtkUnitBase* unit, nint addr, AddonEmjObservation obs)
    {
        if (!emittedFirstLifecycle)
        {
            emittedFirstLifecycle = true;
            findings?.Record("addon_lifecycle", new Dictionary<string, object?>
            {
                ["event"] = eventName,
                ["addon_name"] = addonName,
                ["address"] = addr.ToInt64(),
                ["width"] = obs.Width,
                ["height"] = obs.Height,
                ["is_visible"] = unit->IsVisible,
                ["atk_values_count"] = (int)unit->AtkValuesCount,
            });
        }

        // Schema/timing oddity: addon address is live but RootNode is
        // null. Width and Height fall through to 0 in the observation,
        // and any ImGui-driven highlight code that does math on those
        // dimensions will crash. Emit once per plugin load — same
        // anomaly recurring every tick is the same datapoint.
        if (unit->RootNode == null && !emittedDimensionsZero)
        {
            emittedDimensionsZero = true;
            findings?.Record("addon_dimensions_zero", new Dictionary<string, object?>
            {
                ["addon_name"] = addonName,
                ["address"] = addr.ToInt64(),
                ["event"] = eventName,
                ["is_visible"] = unit->IsVisible,
            });
        }

        // AtkValues anomaly: empty (count=0) or absurdly large
        // (>1024). The former usually means we're inspecting the
        // wrong addon (something else bound to a Mahjong name); the
        // latter suggests memory corruption or a bad read.
        int atkCount = (int)unit->AtkValuesCount;
        if ((atkCount == 0 || atkCount > 1024) && !emittedAtkValuesAnomaly)
        {
            emittedAtkValuesAnomaly = true;
            findings?.Record("atk_values_anomaly", new Dictionary<string, object?>
            {
                ["addon_name"] = addonName,
                ["address"] = addr.ToInt64(),
                ["atk_values_count"] = atkCount,
                ["kind"] = atkCount == 0 ? "empty" : "oversize",
            });
        }
    }

    /// <summary>
    /// Poll the current addon state via GameGui (fallback path when lifecycle events
    /// are not firing, or when the plugin starts with the addon already visible).
    /// Safe to call from the framework thread every tick.
    /// </summary>
    public unsafe AddonEmjObservation Poll()
    {
        if (!addon.TryGet(out var unit, out var resolvedName))
        {
            var missing = AddonEmjObservation.Empty with
            {
                LastSeenUtcTicks = DateTime.UtcNow.Ticks,
                LastLifecycleEvent = LastObservation.LastLifecycleEvent,
            };
            EmitPollPresentChange(false, addonName: null, address: 0);
            LastObservation = missing;
            return missing;
        }

        nint addr = (nint)unit;
        var obs = new AddonEmjObservation(
            Present: true,
            IsVisible: unit->IsVisible,
            Address: addr,
            Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
            Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
            LastSeenUtcTicks: DateTime.UtcNow.Ticks,
            LastLifecycleEvent: LastObservation.LastLifecycleEvent ?? "(poll)");

        // When the addon is already open at plugin load no PostSetup event
        // fires — Dalamud only signals lifecycle events that happen AFTER
        // the listener registers. ~70% of installs in the 2026-05-11 corpus
        // (17/25) shipped zero gameplay logs for this exact reason. Route
        // the Poll-discovered attach through the same finding-emission path
        // Observe uses; the per-session bool gates prevent double-emit on
        // sessions where Observe fired first.
        EmitFirstAttachFindings("poll", resolvedName, unit, addr, obs);

        EmitPollPresentChange(true, resolvedName, addr);
        LastObservation = obs;
        return obs;
    }

    /// <summary>
    /// Emit a finding when Poll()'s reported presence flips. Catches the
    /// scenarios where the addon appears/disappears without a matching
    /// lifecycle event — usually a Dalamud lifecycle-listener bug or a
    /// race during plugin load that left us with a stale observation.
    /// First-ever poll isn't a "change" so it's silently absorbed.
    /// </summary>
    private void EmitPollPresentChange(bool present, string? addonName, nint address)
    {
        if (lastPollPresent == present)
            return;
        var prev = lastPollPresent;
        lastPollPresent = present;
        if (prev is null)
            return;
        findings?.Record("poll_present_change", new Dictionary<string, object?>
        {
            ["from"] = prev.Value,
            ["to"] = present,
            ["addon_name"] = addonName,
            ["address"] = address.ToInt64(),
            ["last_lifecycle_event"] = LastObservation.LastLifecycleEvent,
        });
    }

    /// <summary>
    /// Build a <see cref="StateSnapshot"/> from the current addon state by
    /// delegating to the selected <see cref="IEmjVariant"/>. Returns null when
    /// the addon is absent, not visible, or no registered variant's probe
    /// matches the live layout.
    /// </summary>
    public unsafe StateSnapshot? TryBuildSnapshot()
    {
        if (!addon.TryGet(out var unit, out var resolvedName))
            return null;
        if (!unit->IsVisible)
            return null;

        var variant = selector.Resolve(unit, resolvedName);
        if (variant is null)
            return null;

        // Variant resolved → the layout is now known for this addon. Record
        // it so the memory-dump recorder can include the seat-offset map in
        // each snapshot, even if the snapshot build below fails.
        ActiveLayout = variant.Profile;

        if (EventLogger is null)
            return null;

        var snap = variant.TryBuildSnapshot(
            unit,
            new VariantReadContext(meldTracker, EventLogger));

        // A null here means the variant's probe matched but field reads
        // failed somewhere — the most useful RE signal we can capture,
        // since it says "this client has the right shape but wrong
        // offsets". Emit once per failure streak (and again on recovery)
        // to avoid flooding the corpus on a hard-stuck client.
        if (snap is null && !inSnapshotFailureStreak)
        {
            inSnapshotFailureStreak = true;
            findings?.Record("snapshot_build_fail", new Dictionary<string, object?>
            {
                ["addon_name"] = resolvedName,
                ["variant"] = variant.Name,
                ["addon_address"] = ((nint)unit).ToInt64(),
                ["atk_values_count"] = (int)unit->AtkValuesCount,
            });
        }
        else if (snap is not null && inSnapshotFailureStreak)
        {
            inSnapshotFailureStreak = false;
            findings?.Record("snapshot_build_recover", new Dictionary<string, object?>
            {
                ["addon_name"] = resolvedName,
                ["variant"] = variant.Name,
            });
        }

        return snap;
    }
}
