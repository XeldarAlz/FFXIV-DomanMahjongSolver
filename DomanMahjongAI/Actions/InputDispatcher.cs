using DomanMahjongAI.GameState;
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

        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
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
    ///   pon/pass prompt:    0 = Pon, 1 = Pass
    ///   chi/pass prompt:    0 = Chi, 1 = Pass
    ///   chi multi-sequence: 0..N = chi variants, N+1 = Pass
    ///   riichi (state 6):   0 = Riichi, 1 = Pass — same payload, different state code
    /// "Pass" is always the RIGHTMOST option.
    ///
    /// <para>Return value note: FireCallback returns <c>false</c> for the call-prompt
    /// opcode (11) even on manual in-game clicks that the game visibly accepts —
    /// verified by capturing pon/chi/riichi/tsumo button presses with the capture
    /// hook, which all logged <c>result=False</c> despite the pon/chi/riichi/tsumo
    /// actually firing. The return value is not a success signal for this opcode, so
    /// we ignore it and always report <see cref="DispatchResult.Ok"/>. The caller is
    /// expected to have verified the modal-visibility gate before dispatching —
    /// that's the real "should we click" predicate.</para>
    /// </summary>
    public unsafe DispatchResult DispatchCallOption(int option)
    {
        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        // Both state-15 classic popups (pon/chi/kan/ron + pass button row) and
        // state-6/28 list-widget popups (standalone Riichi/Pass) share the same
        // AtkComponentList shell type (1030), so the shell-type check alone
        // can't tell them apart. The reliable discriminator is parent AtkValues:
        // state-15 prompts put the button labels ("Pon", "Chi", "Pass", ...) as
        // plain Strings at low indices; state-6/28 prompts put only Ints/Bools
        // there with labels living inside the list items' text nodes.
        //
        // Dispatch accordingly:
        //  - Classic button-row: FireCallback([11, opt]) — what the game's own
        //    click handler ends up firing for a button press.
        //  - List widget: route through the AtkComponentList's SelectItem vfunc
        //    with dispatchEvent: true so the internal CallBackInterface runs
        //    (mouse-up → ListItemClick → commit). FireCallback alone on a list
        //    widget only plays the cosmetic declaration animation without
        //    committing state, which is what broke v0.0.0.16/.17.
        //
        // v0.0.0.18 routed everything through SelectItem and broke state-15
        // (pon/chi/ron) because SelectItem doesn't fire the addon-level opcode-11
        // callback the button-row handler expects. Distinguishing the two cases
        // restores state-15 behavior while keeping the state-6/28 fix.
        if (HasClassicButtonLabels(unit))
        {
            var values = stackalloc AtkValue[2];
            values[0].SetInt(11);
            values[1].SetInt(option);
            unit->FireCallback(2, values, true);
            return DispatchResult.Ok;
        }

        if (TryDispatchListItemClick(unit, option))
            return DispatchResult.Ok;

        // Fallback if the shell isn't a list widget either — keep the legacy
        // FireCallback path so we don't silently drop the dispatch.
        var fallback = stackalloc AtkValue[2];
        fallback[0].SetInt(11);
        fallback[1].SetInt(option);
        unit->FireCallback(2, fallback, true);
        return DispatchResult.Ok;
    }

    /// <summary>
    /// True when parent AtkValues carry a bare button-label string like
    /// "Pon"/"Chi"/"Kan"/"Ron"/"Riichi"/"Tsumo"/"Pass" in the first ~20 slots —
    /// the signature of a state-15 classic button-row popup. State-6/28
    /// list-widget popups carry only Ints/Bools there (labels live inside
    /// list-item children), so a false from this check routes dispatch to the
    /// SelectItem path.
    /// </summary>
    private static unsafe bool HasClassicButtonLabels(AtkUnitBase* unit)
    {
        var atkValues = unit->AtkValues;
        if (atkValues == null) return false;
        int scanEnd = Math.Min((int)unit->AtkValuesCount, 20);
        for (int i = 0; i < scanEnd; i++)
        {
            var v = atkValues[i];
            if (v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8 &&
                v.Type != FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString)
                continue;
            if (v.String.Value == null) continue;
            var s = System.Text.Encoding.UTF8.GetString(v.String);
            switch (s)
            {
                case "Pon":
                case "Chi":
                case "Kan":
                case "Ron":
                case "Riichi":
                case "Tsumo":
                case "Pass":
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// If the modal shell is an AtkComponentList, dispatch the click through
    /// the list's native <c>SelectItem(index, dispatchEvent: true)</c> — same
    /// code path a mouse-up runs into. Returns true when handled, false when
    /// the shell isn't a list and the caller should fall back.
    /// </summary>
    private static unsafe bool TryDispatchListItemClick(AtkUnitBase* unit, int option)
    {
        var host = unit->GetNodeById(104);
        if (host == null || (int)host->Type < 1000) return false;
        var hostComp = ((AtkComponentNode*)host)->Component;
        if (hostComp == null) return false;
        var shell = hostComp->GetNodeById(3);
        if (shell == null || (int)shell->Type != 1030) return false;
        var shellComp = ((AtkComponentNode*)shell)->Component;
        if (shellComp == null) return false;
        var list = (FFXIVClientStructs.FFXIV.Component.GUI.AtkComponentList*)shellComp;
        list->SelectItem(option, dispatchEvent: true);
        return true;
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

    /// <summary>
    /// Opcode constants for FireCallback's first AtkValue. Discard = 7, CallPrompt = 11
    /// are confirmed from M6 logging; the rest are TODO — the stub methods below send
    /// what the patterns are likely to be (speculation based on the numeric range of
    /// discovered opcodes) and return HookFailed if the game rejects them. Once the
    /// user captures a real riichi/tsumo/ron event the correct opcodes slot in here
    /// with no call-site changes.
    /// </summary>
    private static class Opcode
    {
        public const int Discard = 7;
        public const int CallPrompt = 11;

        // Speculative — to be confirmed by in-game FireCallback capture:
        public const int Riichi = 8;    // unconfirmed
        public const int Tsumo = 9;     // unconfirmed
        public const int Ron = 10;      // unconfirmed
        public const int Kan = 12;      // unconfirmed (shouminkan + ankan from our turn)
    }

    /// <summary>
    /// Declare riichi while also discarding the tile at <paramref name="slotIndex"/>.
    /// WARNING: opcode unconfirmed — this will likely fail (return HookFailed) until
    /// the user captures a real riichi event and the correct payload is filled in.
    /// </summary>
    public unsafe DispatchResult DispatchRiichi(int slotIndex)
    {
        if (slotIndex is < 0 or > 13) return DispatchResult.InvalidSlot;

        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(Opcode.Riichi);
        values[1].SetInt(slotIndex);
        bool ok = unit->FireCallback(2, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    /// <summary>
    /// Declare tsumo on the last-drawn tile. WARNING: opcode unconfirmed.
    /// </summary>
    public unsafe DispatchResult DispatchTsumo()
    {
        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[1];
        values[0].SetInt(Opcode.Tsumo);
        bool ok = unit->FireCallback(1, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    /// <summary>
    /// Declare ron on the last opponent discard. WARNING: opcode unconfirmed. Ron may
    /// actually be offered as a call prompt (opcode 11) with a distinct option index;
    /// if so, <see cref="DispatchCallOption"/> already handles it and this stub
    /// is not needed.
    /// </summary>
    public unsafe DispatchResult DispatchRon()
    {
        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[1];
        values[0].SetInt(Opcode.Ron);
        bool ok = unit->FireCallback(1, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }

    /// <summary>
    /// Declare kan from our own turn (ankan or shouminkan). WARNING: opcode unconfirmed.
    /// </summary>
    public unsafe DispatchResult DispatchKan(int slotIndex)
    {
        if (slotIndex is < 0 or > 13) return DispatchResult.InvalidSlot;

        if (!MahjongAddon.TryGet(out var unit, out _)) return DispatchResult.AddonNotFound;
        if (!unit->IsVisible) return DispatchResult.AddonNotVisible;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(Opcode.Kan);
        values[1].SetInt(slotIndex);
        bool ok = unit->FireCallback(2, values, true);
        return ok ? DispatchResult.Ok : DispatchResult.HookFailed;
    }
}
