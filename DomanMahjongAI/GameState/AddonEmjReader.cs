using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DomanMahjongAI.Engine;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Finds the <c>Emj</c> addon in the running client, subscribes to its lifecycle
/// events, and exposes:
///   - a raw <see cref="AddonEmjObservation"/> (for diagnostics and the debug overlay)
///   - a <see cref="StateSnapshot"/> builder (stubbed until RE is finished)
///
/// This component must be created on (and disposed from) the framework thread.
/// </summary>
public sealed class AddonEmjReader : IDisposable
{
    public const string AddonName = "Emj";

    private readonly Plugin plugin;
    private bool disposed;

    public AddonEmjObservation LastObservation { get; private set; } = AddonEmjObservation.Empty;

    /// <summary>Fired whenever any lifecycle event updates the observation.</summary>
    public event Action<AddonEmjObservation>? ObservationChanged;

    public AddonEmjReader(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnPostSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnPreFinalize);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnPostRefresh);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, AddonName, OnPostReceiveEvent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        Plugin.AddonLifecycle.UnregisterListener(OnPostSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnPreFinalize);
        Plugin.AddonLifecycle.UnregisterListener(OnPostRefresh);
        Plugin.AddonLifecycle.UnregisterListener(OnPostReceiveEvent);
    }

    private void OnPostSetup(AddonEvent type, AddonArgs args) => Observe("PostSetup", args);
    private void OnPostRefresh(AddonEvent type, AddonArgs args) => Observe("PostRefresh", args);
    private void OnPostReceiveEvent(AddonEvent type, AddonArgs args) => Observe("PostReceiveEvent", args);

    private void OnPreFinalize(AddonEvent type, AddonArgs args)
    {
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
        }

        LastObservation = obs;
        ObservationChanged?.Invoke(obs);
    }

    /// <summary>
    /// Poll the current addon state via GameGui (fallback path when lifecycle events
    /// are not firing, or when the plugin starts with the addon already visible).
    /// Safe to call from the framework thread every tick.
    /// </summary>
    public unsafe AddonEmjObservation Poll()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero)
        {
            var missing = AddonEmjObservation.Empty with
            {
                LastSeenUtcTicks = DateTime.UtcNow.Ticks,
                LastLifecycleEvent = LastObservation.LastLifecycleEvent,
            };
            LastObservation = missing;
            return missing;
        }

        var unit = (AtkUnitBase*)addr;
        var obs = new AddonEmjObservation(
            Present: true,
            IsVisible: unit->IsVisible,
            Address: addr,
            Width: unit->RootNode != null ? unit->RootNode->Width : (ushort)0,
            Height: unit->RootNode != null ? unit->RootNode->Height : (ushort)0,
            LastSeenUtcTicks: DateTime.UtcNow.Ticks,
            LastLifecycleEvent: LastObservation.LastLifecycleEvent ?? "(poll)");
        LastObservation = obs;
        return obs;
    }

    /// <summary>
    /// Build a <see cref="StateSnapshot"/> from the current addon state.
    /// Returns null until the AddonEmj struct is fully reverse-engineered.
    /// </summary>
    public unsafe StateSnapshot? TryBuildSnapshot()
    {
        // TODO(M4): populate real fields once AddonEmjStruct offsets are nailed down.
        var obs = Poll();
        if (!obs.Present) return null;
        return StateSnapshot.Empty;
    }
}
