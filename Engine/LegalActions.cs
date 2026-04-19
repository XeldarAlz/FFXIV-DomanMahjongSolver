namespace DomanMahjongAI.Engine;

[Flags]
public enum ActionFlags
{
    None       = 0,
    Discard    = 1 << 0,
    Riichi     = 1 << 1,
    Tsumo      = 1 << 2,
    Ron        = 1 << 3,
    Pon        = 1 << 4,
    Chi        = 1 << 5,
    AnKan      = 1 << 6,     // concealed kan (from hand)
    MinKan     = 1 << 7,     // open kan from opponent discard
    ShouMinKan = 1 << 8,     // added kan from pon
    Pass       = 1 << 9,     // skip a call opportunity
}

/// <summary>
/// A potential call action offered to us: the triplet/run we'd form if we claim
/// an opponent's discard (or announce a self-kan). Populated by the AddonEmjReader
/// from the game-side UI state.
/// </summary>
public readonly record struct MeldCandidate(
    MeldKind Kind,
    Tile ClaimedTile,
    Tile[] HandTiles,        // the tiles we already hold that complete the meld
    int FromSeat);           // seat of the opponent whose discard we'd claim (-1 for self-kan)

public sealed record LegalActions(
    ActionFlags Flags,
    IReadOnlyList<Tile> DiscardableTiles,
    IReadOnlyList<MeldCandidate> PonCandidates,
    IReadOnlyList<MeldCandidate> ChiCandidates,
    IReadOnlyList<MeldCandidate> KanCandidates)
{
    public static LegalActions None { get; } = new(
        ActionFlags.None, [], [], [], []);

    public bool Can(ActionFlags flag) => (Flags & flag) != 0;
}
