using System;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks;

/// <summary>
/// Builds the live <see cref="IDiscardCapture"/> by trying strategies in
/// preference order — native asm hook first, addon-poll fallback second,
/// inert null-object last. Disposes any failed attempt before returning the
/// next candidate; the caller only ever sees one capture instance.
/// </summary>
public static class DiscardCaptureFactory
{
    public static IDiscardCapture Create(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        StateAggregator aggregator,
        SeatPoolRegistry? seatPools = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(sigScanner);
        ArgumentNullException.ThrowIfNull(aggregator);

        var native = new NativeAsmDiscardCapture(log, framework, sigScanner, seatPools);
        if (native.Health == HookHealth.Active)
            return native;
        native.Dispose();

        log.Warning(
            "[DiscardCapture] native asm hook unavailable — falling back to addon-poll. " +
            "Discard timing will lag by ~one snapshot tick.");
        var fallback = new AddonPollDiscardCapture(log);
        aggregator.Changed += fallback.OnSnapshotChanged;
        return fallback;
    }
}
