using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Resolves the Doman Mahjong addon across the names it goes by on different clients.
/// Known variants (issue #13): most clients name it <c>Emj</c>; an NA/English/non-Steam
/// client was reported showing only <c>EmjL</c> with no <c>Emj</c> present at all.
/// Rather than hardcode one literal across ~20 call sites, every lookup routes through
/// here so a future rename is a one-line change to <see cref="CandidateNames"/>.
///
/// <para>Resolution is lazy: <see cref="TryGet"/> probes <see cref="CandidateNames"/>
/// in order, caches the first name that resolves, and logs once so the chosen name is
/// visible in <c>/xllog</c>. If the cached name stops resolving (e.g. after a patch
/// that renames the addon again), the next call falls back to the full scan and
/// re-logs the new winner.</para>
/// </summary>
internal static class MahjongAddon
{
    /// <summary>
    /// Names the in-match addon has been observed to use, in preferred probe order.
    /// Extend this list if another region/client variant shows up.
    /// </summary>
    public static readonly IReadOnlyList<string> CandidateNames = new[] { "Emj", "EmjL" };

    private static string? lastResolved;

    /// <summary>
    /// Try to resolve the current Mahjong addon. On success, <paramref name="unit"/>
    /// is the live <see cref="AtkUnitBase"/> pointer and <paramref name="name"/> is
    /// the candidate it was found under. Callers still need to check
    /// <c>unit-&gt;IsVisible</c> if they care about modal / in-match state.
    /// </summary>
    public static unsafe bool TryGet(out AtkUnitBase* unit, out string name)
    {
        // Fast path: last known good name. Skips the redundant probe of the losing
        // candidate on every tick once detection has settled on one variant.
        if (lastResolved is not null)
        {
            var ptr = Plugin.GameGui.GetAddonByName(lastResolved);
            if (ptr.Address != nint.Zero)
            {
                unit = (AtkUnitBase*)ptr.Address;
                name = lastResolved;
                return true;
            }
        }

        foreach (var candidate in CandidateNames)
        {
            var ptr = Plugin.GameGui.GetAddonByName(candidate);
            if (ptr.Address == nint.Zero) continue;

            if (lastResolved != candidate)
            {
                Plugin.Log.Info(
                    $"[MjAuto] Mahjong addon resolved as \"{candidate}\" " +
                    $"(candidates: {string.Join(", ", CandidateNames)})");
                lastResolved = candidate;
            }
            unit = (AtkUnitBase*)ptr.Address;
            name = candidate;
            return true;
        }

        unit = null;
        name = string.Empty;
        return false;
    }

    /// <summary>
    /// True if <paramref name="addonName"/> matches any known Mahjong addon name.
    /// Used by hook detours (e.g. <see cref="InputEventLogger"/>) where the addon
    /// name arrives as a string rather than via <see cref="TryGet"/>.
    /// </summary>
    public static bool IsMahjongAddon(string addonName)
    {
        foreach (var c in CandidateNames)
            if (addonName == c) return true;
        return false;
    }
}
