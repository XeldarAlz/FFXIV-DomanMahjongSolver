using System;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Hooks.Strategies;
using Mahjong.Plugin.Dalamud.Telemetry;
using Mahjong.Plugin.Game;

namespace Mahjong.Plugin.Dalamud.Hooks;

/// <summary>
/// Builds the live <see cref="IDiscardCapture"/>. Currently always returns
/// <see cref="AddonPollDiscardCapture"/>; the native asm hook stays offline
/// because the 2026-04-27 sig collides with idle code on post-2026-05
/// FFXIV builds (real-world telemetry: 10 captured "discards" all with
/// <c>tile_id = 0</c>, fired ~5 minutes before the addon was even live).
///
/// <para>The discard-handler signature still gets recorded to the
/// <c>sigprobes</c> telemetry stream so we can track when (and which)
/// FFXIV patches break the pattern — but via <see cref="SigscanProbe"/>
/// rather than constructing the full asm strategy and immediately
/// disposing it. The previous construct-and-dispose dance left a brief
/// window where the asm hook was actually live, polluting the
/// <see cref="SeatPoolRegistry"/> with stale R14 values from idle game
/// code (the 2026-05-08 corpus shows 597 such entries on one install).</para>
///
/// <para>To re-enable native-asm once a verified discard-handler sig lands:
/// instantiate <see cref="NativeAsmDiscardCapture"/> directly, return it
/// when its <see cref="IDiscardCapture.Health"/> reports
/// <see cref="HookHealth.Active"/>, and only fall through to
/// <see cref="AddonPollDiscardCapture"/> on miss.</para>
/// </summary>
public static class DiscardCaptureFactory
{
    public static IDiscardCapture Create(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        StateAggregator aggregator,
        SeatPoolRegistry? seatPools = null,
        ISigprobeLog? sigprobes = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(sigScanner);
        ArgumentNullException.ThrowIfNull(aggregator);
        _ = seatPools; // unused while native-asm strategy is offline

        // Probe the discard handler pattern for the sigprobes corpus.
        // Side-effect-free — no asm hook is installed, so no spurious
        // SeatPoolRegistry entries land while the strategy is offline.
        SigscanProbe.ProbeDiscardHandler(sigScanner, sigprobes ?? NullSigprobeLog.Instance);

        log.Info(
            "[DiscardCapture] using addon-poll strategy (sigscan recorded for telemetry; " +
            "asm hook disabled until a verified discard-handler sig lands).");
        var fallback = new AddonPollDiscardCapture(log);
        aggregator.Changed += fallback.OnSnapshotChanged;
        return fallback;
    }
}
