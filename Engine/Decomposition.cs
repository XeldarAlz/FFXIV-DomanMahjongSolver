namespace DomanMahjongAI.Engine;

public enum GroupKind : byte
{
    Run,       // three consecutive suited tiles (shuntsu)
    Triplet,   // three of a kind (koutsu)
    Kan,       // four of a kind
    Pair,      // two of a kind (head)
}

public enum DecompositionForm : byte
{
    Standard,
    Chiitoitsu,
    Kokushi,
}

/// <summary>
/// A single group of tiles within a decomposition.
/// <see cref="First"/> is the anchor — lowest tile for runs, the tile itself for
/// triplets/kans/pairs.
/// </summary>
public readonly record struct Group(
    GroupKind Kind,
    Tile First,
    bool IsOpen,
    bool IsCompletedByWinningTile = false)
{
    public int TileCount => Kind switch
    {
        GroupKind.Run => 3,
        GroupKind.Triplet => 3,
        GroupKind.Kan => 4,
        GroupKind.Pair => 2,
        _ => 0,
    };

    public bool IsConcealedTriplet => Kind == GroupKind.Triplet && !IsOpen;
    public bool IsConcealedKan => Kind == GroupKind.Kan && !IsOpen;

    public bool ContainsTerminalOrHonor
    {
        get
        {
            if (Kind == GroupKind.Run)
            {
                // A run contains a terminal iff it starts at 1 or ends at 9 (i.e., contains the 1 or 9 of its suit).
                int pos = First.Id % 9;
                return pos == 0 || pos == 6;
            }
            return First.IsTerminalOrHonor;
        }
    }

    public bool AllTerminalOrHonor
    {
        get
        {
            // Only possible for non-run groups (runs always contain a simple tile unless... never, 3-consecutive can't be all terminals).
            return Kind != GroupKind.Run && First.IsTerminalOrHonor;
        }
    }

    public Tile[] Tiles => Kind switch
    {
        GroupKind.Run => [First, Tile.FromId(First.Id + 1), Tile.FromId(First.Id + 2)],
        GroupKind.Pair => [First, First],
        GroupKind.Triplet => [First, First, First],
        GroupKind.Kan => [First, First, First, First],
        _ => [],
    };

    public bool ContainsTile(Tile t)
    {
        return Kind switch
        {
            GroupKind.Run => t.Id >= First.Id && t.Id <= First.Id + 2,
            _ => t.Id == First.Id,
        };
    }

    public static Group FromMeld(Meld m, bool completedByWin = false)
    {
        return m.Kind switch
        {
            MeldKind.Chi => new Group(GroupKind.Run, m.Tiles[0], IsOpen: true, completedByWin),
            MeldKind.Pon => new Group(GroupKind.Triplet, m.Tiles[0], IsOpen: true, completedByWin),
            MeldKind.MinKan or MeldKind.ShouMinKan =>
                new Group(GroupKind.Kan, m.Tiles[0], IsOpen: true, completedByWin),
            MeldKind.AnKan =>
                new Group(GroupKind.Kan, m.Tiles[0], IsOpen: false, completedByWin),
            _ => throw new InvalidOperationException($"unknown meld kind {m.Kind}"),
        };
    }
}

/// <summary>
/// One valid decomposition of a 14-tile winning hand.
/// <list type="bullet">
/// <item>Standard: 5 groups — 4 sets + 1 pair.</item>
/// <item>Chiitoitsu: 7 pair groups.</item>
/// <item>Kokushi: a single "pseudo" representation — Groups may be empty, relevant info is in the source hand.</item>
/// </list>
/// </summary>
public sealed record Decomposition(
    DecompositionForm Form,
    IReadOnlyList<Group> Groups,
    bool IsMenzen,
    Tile WinningTile,
    bool WinningTileFromOpponent)
{
    public Group Pair => Groups.First(g => g.Kind == GroupKind.Pair);
    public IEnumerable<Group> Sets => Groups.Where(g => g.Kind != GroupKind.Pair);

    public int ConcealedTripletCount =>
        Groups.Count(g => g.Kind == GroupKind.Triplet && !g.IsOpen
                          && (!g.IsCompletedByWinningTile || !WinningTileFromOpponent));

    public int KanCount => Groups.Count(g => g.Kind == GroupKind.Kan);
}
