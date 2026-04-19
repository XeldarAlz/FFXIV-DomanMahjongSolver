namespace DomanMahjongAI.Engine;

public enum WinKind
{
    Tsumo,
    Ron,
}

/// <summary>
/// Situational context required to score a winning hand.
/// Tiles for round/seat wind use the 34-space ids: 27=E, 28=S, 29=W, 30=N.
/// </summary>
public sealed record WinContext(
    Tile WinningTile,
    WinKind Kind,

    // Riichi family
    bool IsRiichi = false,
    bool IsDoubleRiichi = false,
    bool IsIppatsu = false,

    // Terminal-of-round / wall-edge situational yaku
    bool IsRinshan = false,      // tsumo'd off a rinshan-draw after kan
    bool IsChankan = false,      // stole a tile from an opponent's shouminkan
    bool IsHaitei = false,       // tsumo on last wall tile
    bool IsHoutei = false,       // ron on last discard
    bool IsTenhou = false,       // dealer's tsumo on first draw
    bool IsChihou = false,       // non-dealer's tsumo on first draw

    // Winds
    int RoundWindTileId = 27,    // 27 (E) or 28 (S) in hanchan
    int SeatWindTileId = 27,     // 27..30

    // Dora
    IReadOnlyList<Tile>? DoraIndicators = null,
    IReadOnlyList<Tile>? UraDoraIndicators = null,

    // Dealer flag
    bool IsDealer = false)
{
    public IReadOnlyList<Tile> Dora => DoraIndicators ?? [];
    public IReadOnlyList<Tile> UraDora => UraDoraIndicators ?? [];

    public Tile RoundWind => Tile.FromId(RoundWindTileId);
    public Tile SeatWind => Tile.FromId(SeatWindTileId);

    public bool IsTsumo => Kind == WinKind.Tsumo;
    public bool IsRon => Kind == WinKind.Ron;
}
