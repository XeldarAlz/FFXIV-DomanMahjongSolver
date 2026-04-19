using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;

namespace DomanMahjongAI.GameState;

/// <summary>
/// Placeholder mirror of the in-game <c>AddonEmj</c> struct — the UI wrapper for
/// Doman Mahjong. Not in ClientStructs yet; offsets will be filled in during
/// RE (ReClass.NET + XivReClassPlugin + IDA/Ghidra) and validated on plugin load
/// against a golden struct hash.
///
/// Currently we only rely on the AtkUnitBase header (shared across every FFXIV
/// addon). Known offsets below are speculative and will be revised.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x300)]
public unsafe struct AddonEmj
{
    [FieldOffset(0x0)] public AtkUnitBase AtkUnitBase;

    // TODO(M4): tile count arrays per seat; discard pools; dora indicators;
    // riichi flags; scores; wall-remaining; turn/dealer; legal-action bitmask.
    // Every field below is a placeholder and must be validated against the
    // running client before any are relied on.

    // [FieldOffset(0x???)] public fixed byte OurHand[14];
    // [FieldOffset(0x???)] public int WallRemaining;
    // [FieldOffset(0x???)] public int DealerSeat;
    // ...
}
