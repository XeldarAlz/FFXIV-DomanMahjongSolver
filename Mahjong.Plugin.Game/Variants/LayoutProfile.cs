namespace Mahjong.Plugin.Game.Variants;

/// <summary>
/// Variant-specific constants for one mahjong-addon layout. Loaded from
/// <c>data/layouts/*.json</c> by <see cref="JsonLayoutProfileLoader"/>; consumed
/// by the Dalamud-coupled variant reader to dereference the right offsets and
/// node IDs without needing client-specific subclasses.
///
/// Adding a new variant (JP, OC, ...) is one JSON file in <c>data/layouts/</c>
/// — never a code change to this project.
/// </summary>
public sealed record LayoutProfile(
    string Name,
    string AddonName,
    int TileTextureBase,
    LayoutOffsets Offsets,
    LayoutNodeIds NodeIds,
    LayoutAtkValueIndices AtkValues,
    LayoutStateCodes StateCodes,
    LayoutSanityLimits Limits);

/// <summary>
/// Byte offsets into the addon's memory (<c>AtkUnitBase*</c>). All four scores
/// and the matching per-seat discard-count bytes follow a consistent stride
/// (~736 bytes / 0x2E0 between seats); the hand array sits at a fixed offset
/// past the score block.
/// </summary>
public sealed record LayoutOffsets(
    int SelfScore,
    int ShimochaScore,
    int ToimenScore,
    int KamichaScore,
    int SelfDiscardCountByte,
    int ShimochaDiscardCountByte,
    int ToimenDiscardCountByte,
    int KamichaDiscardCountByte,
    int HandArrayStart,
    int DoraIndicator);

/// <summary>
/// Atk node IDs for the call-modal popup. The host is the modal container;
/// the shell holds the visible button list — one of them carrying labels like
/// "Pon", "Chi", "Kan", "Riichi", "Tsumo".
/// </summary>
public sealed record LayoutNodeIds(
    uint CallModalHost,
    uint CallModalShell);

/// <summary>
/// Indices into the addon's <c>AtkValues</c> array. The state code drives
/// branching in the variant reader; the wall count and chi-claimed-tile
/// indices are read conditionally based on state.
/// </summary>
public sealed record LayoutAtkValueIndices(
    int StateCode,
    int WallCount,
    int ChiClaimedTile);

/// <summary>
/// Magic numbers for the addon's state-code field. The codes drive call-prompt
/// detection and the discard-mode gate.
/// </summary>
public sealed record LayoutStateCodes(
    int OurTurnDiscard,
    int CallPrompt,
    int CallPromptList,
    int SelfDeclareList,
    int PostDrawIdle);

/// <summary>
/// Plausibility bounds. Probe rejects any read where a score is out of range,
/// a discard count exceeds the cap, or too many populated hand slots fail to
/// decode under the variant's tile texture base.
/// </summary>
public sealed record LayoutSanityLimits(
    int HandSize,
    int WallInitial,
    int ScoreSanityMax,
    int DiscardCountSanityMax,
    int MaxAkadoraSlots);
