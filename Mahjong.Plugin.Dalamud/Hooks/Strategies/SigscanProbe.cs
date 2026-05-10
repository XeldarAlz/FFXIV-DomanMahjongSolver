using System;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Telemetry;

namespace Mahjong.Plugin.Dalamud.Hooks.Strategies;

/// <summary>
/// One-shot signature scanner that records a single result through the
/// sigprobes telemetry stream and exits — no asm hook, no buffer alloc, no
/// transient SeatPoolRegistry side effect.
///
/// <para>Replaces the prior pattern in <see cref="DiscardCaptureFactory"/> of
/// constructing a full <see cref="NativeAsmDiscardCapture"/> just to feed
/// sigprobes and then immediately disposing it. The construct-and-dispose
/// dance left a 4-tick window where the asm hook was actually live, which
/// in the 2026-05 corpus emitted 597 spurious seat_pools entries on one
/// install before the disposal fired.</para>
///
/// <para>Currently only used for the doman discard-handler signature; the
/// signature lives at <see cref="NativeAsmDiscardCapture.DiscardSig"/>. If
/// more signatures need probing in future, add overloads or expose a
/// generic <c>Probe(string sigName, string pattern, ...)</c>.</para>
/// </summary>
internal static class SigscanProbe
{
    /// <summary>
    /// Scan for the doman discard-handler pattern and write one record to
    /// <paramref name="sigprobes"/>. Returns the resolved match address (zero
    /// on miss). Both success and failure produce a sigprobes record so the
    /// corpus tracks pattern drift across FFXIV patches.
    /// </summary>
    public static nint ProbeDiscardHandler(ISigScanner sigScanner, ISigprobeLog sigprobes)
    {
        ArgumentNullException.ThrowIfNull(sigScanner);
        ArgumentNullException.ThrowIfNull(sigprobes);

        const string sigName = "doman.discard-handler";
        const string pattern = NativeAsmDiscardCapture.DiscardSig;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var match = sigScanner.ScanText(pattern);
            sw.Stop();
            sigprobes.Record(
                sigName: sigName,
                pattern: pattern,
                matchAddress: match,
                elapsedMs: sw.Elapsed.TotalMilliseconds,
                success: true);
            return match;
        }
        catch (Exception ex)
        {
            sw.Stop();
            sigprobes.Record(
                sigName: sigName,
                pattern: pattern,
                matchAddress: 0,
                elapsedMs: sw.Elapsed.TotalMilliseconds,
                success: false,
                errorMessage: ex.Message);
            return 0;
        }
    }
}
