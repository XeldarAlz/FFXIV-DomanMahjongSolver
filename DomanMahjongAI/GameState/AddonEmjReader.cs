using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using DomanMahjongAI.Engine;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

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
    /// Populates the fields we have from the M4 RE work: hand tiles + per-seat scores.
    /// Wall count, turn owner, dealer, discard pools, round — still unmapped.
    /// </summary>
    /// <remarks>
    /// Offset layout (AddonEmj relative to addon base):
    ///   +0x0500   self score (int32)
    ///   +0x07E0   shimocha score
    ///   +0x0AC0   toimen score
    ///   +0x0DA0   kamicha score
    ///   +0x0DB8   14 hand-tile slots, 4 bytes each: [tile_id + 9, 0x29, 0x01, 0x00]
    ///             Slots 0-12 sorted ascending; slot 13 holds the last-drawn tile when 14.
    /// </remarks>
    public unsafe StateSnapshot? TryBuildSnapshot()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero) return null;

        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible) return null;

        byte* basePtr = (byte*)addr;

        // Hand tiles at +0x0DB8.
        var hand = new List<Tile>(14);
        for (int i = 0; i < 14; i++)
        {
            byte raw = basePtr[0xDB8 + i * 4];
            if (raw == 0) break;    // empty slot
            int tileId = raw - 9;
            if (tileId < 0 || tileId >= Tile.Count34) continue;
            hand.Add(Tile.FromId(tileId));
        }

        // Scores at the known seat offsets (seat-relative: [self, shimocha, toimen, kamicha]).
        var scores = new int[4]
        {
            *(int*)(basePtr + 0x0500),
            *(int*)(basePtr + 0x07E0),
            *(int*)(basePtr + 0x0AC0),
            *(int*)(basePtr + 0x0DA0),
        };
        // Reject garbage reads (game hasn't populated the struct yet).
        bool plausibleScores = scores.All(s => s is >= 0 and <= 200000);
        if (!plausibleScores) return null;

        // Read wall count from AtkValues[1] when state code in [0] == 5 (post-draw idle).
        // Otherwise keep the default (snapshot consumers should tolerate stale).
        int wallRemaining = StateSnapshot.Empty.WallRemaining;
        var atkValues = unit->AtkValues;
        if (atkValues != null && unit->AtkValuesCount >= 2
            && atkValues[0].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int
            && atkValues[0].Int == 5)
        {
            int reported = atkValues[1].Int;
            if (reported is > 0 and <= 136) wallRemaining = reported;
        }

        // Assemble the snapshot. Seat-relative: self is always index 0 here.
        // RoundWind / OurSeat (in the absolute E/S/W/N sense) aren't reliably recoverable
        // yet — leave them at the defaults; downstream scorers will treat the player as
        // East for yakuhai purposes (minor inaccuracy, fixable when M4 sig-scan lands).
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView([], [], [], false, -1, false, false);

        var legal = hand.Count == 14
            ? new LegalActions(ActionFlags.Discard, [], [], [], [])
            : LegalActions.None;

        return StateSnapshot.Empty with
        {
            Hand = hand,
            Scores = scores,
            Seats = seats,
            WallRemaining = wallRemaining,
            Legal = legal,
        };
    }
}
