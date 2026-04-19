using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace DomanMahjongAI.Actions;

/// <summary>
/// Sends input events to the <c>Emj</c> addon via <c>AtkUnitBase.FireCallback</c>.
/// All calls must be made from the framework thread.
///
/// Callback patterns discovered during M6 logging (see <c>memory/project_addon_emj_re_notes.md</c>):
/// <list type="bullet">
///   <item><description>Discard tile at slot N (0-13): <c>FireCallback([Int=7, Int=N])</c></description></item>
///   <item><description>Pass on a call prompt:      <c>FireCallback([Int=11, Int=0])</c></description></item>
/// </list>
/// Pon/Chi/Kan/Riichi/Tsumo/Ron patterns are still unmapped — need a logging session
/// where the user actually triggers those actions.
/// </summary>
public sealed class InputDispatcher
{
    private const string AddonName = "Emj";

    public enum DispatchResult
    {
        Ok,
        AddonNotFound,
        AddonNotVisible,
        InvalidSlot,
        HookFailed,         // FireCallback returned false (wrong state / invalid args)
    }

    /// <summary>
    /// Discard the tile at the given closed-hand slot (0..13). Slot 13 = last-drawn tile.
    /// FireCallback returns false on invalid state; we surface that as HookFailed rather
    /// than crashing (unlike ReceiveEvent synthesis).
    /// </summary>
    public unsafe DispatchResult DispatchDiscard(int slotIndex)
    {
        if (slotIndex is < 0 or > 13) return DispatchResult.InvalidSlot;

        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero) return DispatchResult.AddonNotFound;

        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(7);
        values[1].SetInt(slotIndex);
        bool ok = unit->FireCallback(2, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    /// <summary>
    /// Select option <paramref name="option"/> on the currently-active call prompt.
    /// Option numbers are button-order (leftmost = 0):
    ///   pon/pass prompt: 0 = Pon, 1 = Pass
    ///   chi/pass prompt: 0 = Chi, 1 = Pass
    ///   chi with multiple sequences: 0..N = chi variants, N+1 = Pass
    /// So "Pass" is always the RIGHTMOST option. Use <see cref="DispatchPass"/>
    /// with an explicit option-count if you know the prompt shape, otherwise the
    /// auto-pass loop must read AtkValues to determine how many options are shown.
    /// </summary>
    public unsafe DispatchResult DispatchCallOption(int option)
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        nint addr = ptr.Address;
        if (addr == nint.Zero) return DispatchResult.AddonNotFound;

        var unit = (AtkUnitBase*)addr;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(11);
        values[1].SetInt(option);
        bool ok = unit->FireCallback(2, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    /// <summary>
    /// Pass on a call prompt. Option 1 = Pass (rightmost button). Confirmed by observation:
    /// pon/pass and chi/pass prompts both show [Call][Pass] order, so pass is always opt 1.
    /// No fallback — if this fails we return HookFailed; fallback to option 0 would
    /// accidentally fire the call action (undesired).
    /// </summary>
    public DispatchResult DispatchPass() => DispatchCallOption(1);

    /// <summary>
    /// Accept a pon/chi/kan call by clicking the leftmost button (option 0). The game
    /// knows from context which call is offered — we just fire option 0. For chi
    /// prompts with multiple sequence variants, option 0 picks the first (lowest)
    /// sequence; we'd need a specific override for non-default variants.
    /// </summary>
    public DispatchResult DispatchCall() => DispatchCallOption(0);

    /// <summary>
    /// Find the slot index (0..13) of a given tile in the hand. Returns -1 if not found.
    /// For duplicate tiles, prefers the last-drawn slot (13) if the tile matches there,
    /// otherwise the lowest sorted slot.
    /// </summary>
    public static int FindSlotOfTile(Engine.Tile target, System.Collections.Generic.IReadOnlyList<Engine.Tile> hand)
    {
        if (hand.Count == 14 && hand[13].Id == target.Id) return 13;
        for (int i = 0; i < hand.Count; i++)
            if (hand[i].Id == target.Id) return i;
        return -1;
    }

}
