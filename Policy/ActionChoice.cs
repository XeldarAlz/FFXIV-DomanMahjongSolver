using DomanMahjongAI.Engine;

namespace DomanMahjongAI.Policy;

public enum ActionKind : byte
{
    Pass,
    Discard,
    Riichi,
    Tsumo,
    Ron,
    Pon,
    Chi,
    AnKan,
    MinKan,
    ShouMinKan,
}

/// <summary>
/// A policy's final decision on the current turn. Immutable. The
/// <see cref="Reasoning"/> string is human-readable trace for the debug overlay
/// and shouldn't affect logic.
/// </summary>
public sealed record ActionChoice(
    ActionKind Kind,
    Tile? DiscardTile = null,
    MeldCandidate? Call = null,
    string Reasoning = "")
{
    public static ActionChoice Pass(string why = "") =>
        new(ActionKind.Pass, Reasoning: why);

    public static ActionChoice Discard(Tile t, string why = "") =>
        new(ActionKind.Discard, DiscardTile: t, Reasoning: why);

    public static ActionChoice DeclareRiichi(Tile discard, string why = "") =>
        new(ActionKind.Riichi, DiscardTile: discard, Reasoning: why);

    public static ActionChoice DeclareTsumo(string why = "") =>
        new(ActionKind.Tsumo, Reasoning: why);

    public static ActionChoice DeclareRon(string why = "") =>
        new(ActionKind.Ron, Reasoning: why);
}
